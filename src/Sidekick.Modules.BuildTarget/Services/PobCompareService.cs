using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 试穿对比：把「这件装备」挂进基准 BD 重算一次，给出 DPS / EHP 的增减。
///
/// 调用链：模板里的 BD 源码 → helper 载入（同一模板只载一次）→ 物品转英文文本 →
/// `equip` 到对应槽位 → 与基线求差。
///
/// 三条纪律（都对应实测踩过的坑）：
///   1. **不是所有物品都能算**：基底取不到英文时 PoB 会直接抛 Lua 错（实测 Item.lua:1867），
///      所以转换层报 <c>BaseIdentified=false</c> 时我们直接返回状态，不把报错丢给用户。
///   2. **两类缺失都要带出来、而且要分成两个数**：我们没认出的（转换层缺口）与引擎不支持的
///      （PoB 只算它支持的修饰词）—— 它们都没参与计算，界面不许替用户断言方向。
///   3. **请求串行**：helper 是单通道 stdio，同时发两条会把响应错位（客户端也有一层闸，这里是语义层的）。
///
/// 除「一整件装备的对比」外，本服务还对外提供 <see cref="MeasureTextAsync"/>：
/// **把任意一段已经转换好的物品文本试穿并读回数值**（C2b 的 top5 收益词缀要靠它跑 N+1 次）。
/// 两条路径共用同一把闸、同一份基线缓存与同一套错误语义，避免出现「第二条路径少了自愈」这种分叉。
/// </summary>
public class PobCompareService(
    PobEngineClient engine,
    PobItemTextService itemText,
    BuildTargetOptionsStore options,
    ILogger<PobCompareService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>「引擎里已载入哪份 BD」的缓存（键 = 模板 id + 引擎代际号 + 评估场景，见 <see cref="BaselineCache"/>）。</summary>
    private readonly BaselineCache baselineCache = new();

    /// <summary>侧边栏的部位键 → PoB 的槽位名（见 Engine/README.md，权威来源是 PoB 的 ItemsTab.baseSlots）。</summary>
    private static readonly Dictionary<string, string> PobSlotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["helmet"] = "Helmet",
        ["bodyarmour"] = "Body Armour",
        ["gloves"] = "Gloves",
        ["boots"] = "Boots",
        ["amulet"] = "Amulet",
        ["ring1"] = "Ring 1",
        ["ring2"] = "Ring 2",
        ["belt"] = "Belt",
        ["weapon"] = "Weapon 1",
        ["offhand"] = "Weapon 2",
        // 药剂 / 咒符：槽键与 PoB 槽名 1:1（见 SlotKeys 的口径说明）
        ["flask1"] = "Flask 1",
        ["flask2"] = "Flask 2",
        ["charm1"] = "Charm 1",
        ["charm2"] = "Charm 2",
        ["charm3"] = "Charm 3",
    };

    public static string? MapSlot(string? slotKey) =>
        slotKey != null && PobSlotNames.TryGetValue(slotKey, out var name) ? name : null;

    /// <summary>
    /// 当前评估场景（<see cref="BuildTargetOptionsStore.PobContext"/> 的现值）。
    /// 备选篮的「前提」要照抄这一份 —— 场景是试穿数值的一条前提（见 <see cref="BasketPremise"/>）。
    /// </summary>
    public string Context => options.PobContext;

    /// <summary>
    /// 引擎进程的代际号（引擎每重启一次 +1，见 <see cref="PobEngineClient.Generation"/>）。
    /// 代际号既是基线缓存的键的一部分，也是备选篮「前提」的一部分：
    /// 引擎重启过 = 那批数字不是同一代算出来的。
    /// </summary>
    public int EngineGeneration => engine.Generation;

    /// <summary>
    /// 模板 + 当前场景的**基线缓存里那份数值**用的伤害指标键；没载入过（或换了模板 / 场景）时为 null。
    ///
    /// 备选篮的「前提」里要带主指标（<c>TotalDPS</c> / <c>CombinedDPS</c> / <c>FullDPS</c>）：
    /// 回退到 Combined / Full 时，绝对值与默认口径**不是一回事**（见 <see cref="PobPrimaryMetric"/>）。
    /// 缓存里没有对应基线就是「不知道」→ 返回 null；调用方拿它与记录的那一份比，
    /// 不一致就按「前提变了」处理（不许猜一个值出来）。
    /// </summary>
    public string? BaselineMetricKey(BuildTargetTemplate? template) =>
        template != null && baselineCache.IsValid(template.Id, engine.Generation, options.PobContext)
            ? PobPrimaryMetric.Select(baselineCache.Stats!).Key
            : null;

    public async Task<PobCompareResult> CompareAsync(
        BuildTargetTemplate? template,
        Item? item,
        string? slotKey,
        CancellationToken cancellationToken = default)
    {
        var guard = Guard(template, slotKey);
        if (guard != null)
        {
            return PobCompareResult.Not(guard.Value.Status, guard.Value.Error);
        }

        var pobSlot = MapSlot(slotKey)!;

        var conversion = await ConvertAsync(item, cancellationToken);
        if (conversion.Status != PobCompareStatus.Success)
        {
            return PobCompareResult.Not(conversion.Status, conversion.Error);
        }

        var stopwatch = Stopwatch.StartNew();

        await gate.WaitAsync(cancellationToken);
        try
        {
            var measured = await EquipAndMeasureAsync(template!, pobSlot, conversion.Text!, cancellationToken);
            if (measured.Stats is not { } current)
            {
                return PobCompareResult.Not(measured.Status, measured.Error);
            }

            stopwatch.Stop();

            // 这次用的是哪个伤害指标（TotalDPS 是默认；回退到 Combined/Full 时要能从日志里看出来）
            var metric = PobPrimaryMetric.Select(measured.Baseline!);
            logger.LogInformation(
                "[BuildTarget] PoB compare {Slot}: dps {BaseDps:0} → {Dps:0}, ehp {BaseEhp:0} → {Ehp:0} ({Elapsed:0} ms, unmapped {Unmapped}, annotation-stripped {Annotated}, metric {Metric})",
                pobSlot,
                PobPrimaryMetric.Value(measured.Baseline!, metric.Key),
                PobPrimaryMetric.Value(current, metric.Key),
                measured.Baseline.Ehp,
                current.Ehp,
                stopwatch.ElapsedMilliseconds,
                conversion.Result!.Skipped,
                conversion.Result.AnnotationStripped,
                // 算不出伤害时 Select 给的是空串 —— 日志里写成 none，免得看起来像漏打了一个字段
                string.IsNullOrEmpty(metric.Key) ? "none" : metric.Key);

            return PobCompareResult.Ok(
                measured.Baseline,
                current,
                conversion.Result.Skipped,
                measured.EngineUnsupported);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] PoB compare failed");
            return PobCompareResult.Not(PobCompareStatus.Failed, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 试穿**一段已经转换好的**物品文本并读回数值（C2b 的逐条词缀实验靠它）。
    ///
    /// 只做「引擎可不可用 / 能不能载入基线 / 这次试穿成不成功」这三件事，**不做物品转换** ——
    /// 转换那一步（以及它带来的 BaseUnknown / ConversionFailed 语义）由调用方自己在
    /// <see cref="PobCompareService"/> 的公开路径上处理。
    /// </summary>
    public async Task<PobMeasure> MeasureTextAsync(
        BuildTargetTemplate? template,
        string? pobSlot,
        string itemText,
        CancellationToken cancellationToken = default)
    {
        if (!options.PobEngine)
        {
            return PobMeasure.Not(PobCompareStatus.Disabled);
        }

        if (template == null || string.IsNullOrWhiteSpace(pobSlot))
        {
            return PobMeasure.Not(PobCompareStatus.NoTemplate);
        }

        if (string.IsNullOrWhiteSpace(template.PobXml))
        {
            return PobMeasure.Not(PobCompareStatus.NoBuildXml);
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var measured = await EquipAndMeasureAsync(template, pobSlot, itemText, cancellationToken);
            return measured.Stats is { } stats
                ? PobMeasure.Succeeded(measured.Baseline!, stats, measured.EngineUnsupported)
                : PobMeasure.Not(measured.Status, measured.Error, measured.EngineUnsupported);
        }
        catch (OperationCanceledException)
        {
            throw;   // 调用方取消不是「引擎出错」，交给上层（C2a 的批量取消也走这条）
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] PoB measure failed");
            return EngineFailure(ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 开工前的守卫：把「开关没开 / 没模板 / 模板没 BD 源码 / 珠宝 / 不在槽位体系」这几种
    /// **不用起引擎就能判**的情况统一成状态，两条公开路径共用。
    /// </summary>
    private (PobCompareStatus Status, string? Error)? Guard(BuildTargetTemplate? template, string? slotKey)
    {
        if (!options.PobEngine)
        {
            return (PobCompareStatus.Disabled, null);
        }

        if (template == null)
        {
            return (PobCompareStatus.NoTemplate, null);
        }

        if (string.IsNullOrWhiteSpace(template.PobXml))
        {
            return (PobCompareStatus.NoBuildXml, null);
        }

        if (slotKey == SlotKeys.Jewel)
        {
            // 珠宝在 PoB 里是天赋树上的镶嵌孔（SocketIdURL nodeId），没有槽位名可传；
            // 要试穿得先选一个孔，本版不做 —— 如实说「暂不支持」，不要退回泛泛的「不在槽位体系里」。
            return (PobCompareStatus.SocketedItem, null);
        }

        if (MapSlot(slotKey) == null)
        {
            return (PobCompareStatus.UnsupportedSlot, null);
        }

        return null;
    }

    /// <summary>中文物品 → 英文 raw 文本（含基底取不到的判定）。</summary>
    private async Task<(PobCompareStatus Status, string? Text, PobItemText.BuildResult? Result, string? Error)> ConvertAsync(
        Item? item,
        CancellationToken cancellationToken)
    {
        if (item == null)
        {
            return (PobCompareStatus.NoTemplate, null, null, null);
        }

        var text = await itemText.BuildAsync(item);
        if (text == null)
        {
            return (PobCompareStatus.ConversionFailed, null, null, null);
        }

        if (!text.BaseIdentified)
        {
            // PoB 认不出中文基底（它内部会拿 baseName 拼接然后炸），这里提前拦住
            return (PobCompareStatus.BaseUnknown, null, null, null);
        }

        return (PobCompareStatus.Success, text.Text, text, null);
    }

    /// <summary>「试试这件东西」的全部引擎动作：起引擎 → 载基线 → equip → 读数值。</summary>
    private async Task<(PobCompareStatus Status, PobStats? Baseline, PobStats? Stats, string? Error, IReadOnlyList<string> EngineUnsupported)> EquipAndMeasureAsync(
        BuildTargetTemplate template,
        string pobSlot,
        string itemText,
        CancellationToken cancellationToken)
    {
        if (!await engine.EnsureStartedAsync(cancellationToken))
        {
            return (PobCompareStatus.EngineUnavailable, null, null, engine.LastError, (IReadOnlyList<string>)[]);
        }

        var baseline = await EnsureBaselineAsync(template, cancellationToken);
        if (baseline.Stats is not { } baselineStats)
        {
            // 载入失败的原因由 EnsureBaselineAsync 自己给（引擎起不来 / 场景核对不通过 …），
            // 那时的 engine.LastError 往往是空的 —— 一律用它的原话，别在界面上留「引擎出错：-」。
            return EngineFailure(null, baseline.Error ?? engine.LastError);
        }

        var response = await EquipAsync(pobSlot, itemText, cancellationToken);

        // 引擎被换代过（例如恰好赶在一次超时重启之后）：Lua 侧会回 "no build loaded"。
        // 兜底重载一次再试 —— 正常路径靠 BaselineCache 的代际号就拦住了，这是最后一道保险
        // （审计 L1：原先这条路径完全没有自愈，一次超时之后每次试穿都永久失败）。
        if (response is not { Ok: true } && IsNoBuildLoaded(response))
        {
            logger.LogWarning("[BuildTarget] Engine reports no build loaded, reloading the baseline once");
            baselineCache.Invalidate();

            var reloaded = await EnsureBaselineAsync(template, cancellationToken);
            if (reloaded.Stats is not { } reloadedStats)
            {
                return EngineFailure(null, reloaded.Error ?? engine.LastError);
            }

            baselineStats = reloadedStats;
            response = await EquipAsync(pobSlot, itemText, cancellationToken);
        }

        if (response is not { Ok: true })
        {
            // PoB 认不出基底时是**抛 Lua 错**（`Classes/Item.lua:1867: attempt to concatenate field 'baseName'`）。
            // 对用户来说这和「基底取不到英文名」是同一件事 —— 别把 Lua 栈丢到界面上
            // （真机冒烟实测：咒符那件就这么显示了整条 Lua 报错）。
            if (IsEngineBaseNameFailure(response))
            {
                logger.LogWarning(
                    "[BuildTarget] Engine could not resolve the item base ({Error}). Item text:\n{Text}",
                    response.Error,
                    itemText);
                return (PobCompareStatus.BaseUnknown, null, null, null, (IReadOnlyList<string>)[]);
            }

            // 失败时把转换出来的文本一起记下来：没有它，下次定位要重跑一遍真机
            logger.LogWarning(
                "[BuildTarget] PoB equip failed: {Error}. Item text:\n{Text}",
                response.Error ?? engine.LastError,
                itemText);
            return EngineFailure(baselineStats, response?.Error ?? engine.LastError);
        }

        var current = ReadStats(response);
        if (current == null)
        {
            return EngineFailure(baselineStats, "engine returned no stats");
        }

        return (PobCompareStatus.Success, baselineStats, current, null, ReadEngineUnsupported(response));
    }

    /// <summary>
    /// 引擎侧的失败，状态要**按「此刻开关是不是开着」判**（第六轮审计「应该修-1」）：
    /// 拨关那一刻在飞的请求会以 `engine closed the connection` 这类错误收尾，
    /// 原样报成「引擎出错：…」就等于把**用户亲手做的事**说成引擎故障 ——
    /// 与本项目「非引擎原因不许说成引擎出错」的既有口径冲突（第二轮 3-3 修过同族）。
    /// 开关关着 → 归成 `Disabled`，界面上的现成文案正好是「先去设置里打开这个开关」。
    /// </summary>
    private (PobCompareStatus Status, PobStats? Baseline, PobStats? Current, string? Error, IReadOnlyList<string> EngineUnsupported) EngineFailure(
        PobStats? baseline,
        string? error) =>
        options.PobEngine
            ? (PobCompareStatus.Failed, baseline, null, error, (IReadOnlyList<string>)[])
            : (PobCompareStatus.Disabled, baseline, null, null, (IReadOnlyList<string>)[]);

    /// <summary>同上，给 <see cref="MeasureTextAsync"/> 用的形态。</summary>
    private PobMeasure EngineFailure(string? error) =>
        options.PobEngine
            ? PobMeasure.Not(PobCompareStatus.Failed, error)
            : PobMeasure.Not(PobCompareStatus.Disabled);

    /// <summary>
    /// 读 helper 回来的「引擎自己不支持的词缀行」（`result.unsupported.lines`）。
    /// 字段缺失 / 形状不对一律当**空**（engine 老版本没有这一项时行为不变），
    /// 但**不许**把「读不出来」当成「没有盲区」以外的解释 —— 所以宁可空，也不编数字。
    /// internal 是为了让单测直接喂一段假应答验形状。
    /// </summary>
    internal static IReadOnlyList<string> ReadEngineUnsupported(PobEngineResponse? response)
    {
        var node = response?.GetObject("unsupported");
        if (node == null || !node.TryGetValue("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. lines.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!),
        ];
    }

    /// <summary>试穿一件物品到某个 PoB 槽位（单次 ~15 ms，超时给 15 s 是上限兜底）。</summary>
    private Task<PobEngineResponse?> EquipAsync(string pobSlot, string itemText, CancellationToken cancellationToken) =>
        engine.SendAsync(
            PobEngineProtocol.MethodEquip,
            new Dictionary<string, object?>
            {
                ["slot"] = pobSlot,
                ["item"] = itemText,
            },
            TimeSpan.FromSeconds(15),
            cancellationToken);

    /// <summary>
    /// Lua 侧「没载入 BD」的报错（<c>tao-engine-server.lua</c> 的 <c>if state.calcFunc == nil then error("no build loaded")</c>）。
    /// 命中它 = 引擎里没有基准，缓存该失效重载，而不是把报错丢给用户。
    /// </summary>
    private static bool IsNoBuildLoaded(PobEngineResponse? response) =>
        response?.Error?.Contains("no build loaded", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>PoB 内部因为 baseName 为空而抛的 Lua 错（等于「引擎认不出这个词基底」）。</summary>
    private static bool IsEngineBaseNameFailure(PobEngineResponse? response) =>
        response?.Error?.Contains("baseName", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 载入基准 BD。同一个模板 + 同一个引擎代次 + **同一个评估场景**只载一次（引擎侧 load_build 约 400 ms）。
    ///
    /// 返回值带上失败原因：载入失败的原因不一定在 <see cref="PobEngineClient.LastError"/> 上
    /// （例如「场景没生效」是我们自己判的），原话带出去才不会在界面上留下「引擎出错：-」。
    /// </summary>
    private async Task<(PobStats? Stats, string? Error)> EnsureBaselineAsync(BuildTargetTemplate template, CancellationToken cancellationToken)
    {
        var context = options.PobContext;

        if (baselineCache.IsValid(template.Id, engine.Generation, context))
        {
            return (baselineCache.Stats, null);
        }

        // ⚠ 代际号要在发请求**之前**记下来：若引擎在载入过程中被杀/重启，
        //   这份数值就不属于新一代引擎，下次调用必须重载（宁可多重载一次，不可用错基准）。
        var generation = engine.Generation;

        var response = await engine.SendAsync(
            PobEngineProtocol.MethodLoadBuild,
            new Dictionary<string, object?>
            {
                ["xml"] = template.PobXml,
                ["name"] = template.Name,
                // 评估场景：Lua 侧在**内存里**临时覆盖敌人的等级 / Boss 标记，不写回 BD 源码
                ["context"] = context,
            },
            TimeSpan.FromSeconds(90),   // 首次要加载引擎数据，给足时间
            cancellationToken);

        if (response is not { Ok: true })
        {
            baselineCache.Invalidate();
            return (null, engine.LastError);
        }

        // 核对 Lua 侧回的「实际生效」标签（`result.context`）。三种形状：
        //   1) 与请求一致 → 正常，记进缓存；
        //   2) **不一致** → 我们手里这份数值不是要的那个场景算出来的。当成刚才请求的场景用
        //      会让面板上的场景标签变成假话（用户以为自己看的是打王数据）→ 如实回失败，不缓存；
        //   3) 老 helper 压根没这个字段（null）→ 只告警：数值是按 BD 自己的配置算的，
        //      分发包里没同步 helper 时就是这个形状（%APPDATA% 那份是手动放的），不能因此把功能整个弄坏。
        var effective = ReadContext(response);
        if (effective != null &&
            !string.Equals(PobContexts.Normalize(effective), context, StringComparison.OrdinalIgnoreCase))
        {
            baselineCache.Invalidate();
            var error = $"engine reported evaluation context '{effective}' for a '{context}' request";
            logger.LogWarning("[BuildTarget] PoB load_build {Error} — refusing to use it as the {Context} baseline", error, context);
            return (null, error);
        }

        if (effective == null && context != PobContexts.Build)
        {
            // ⚠ 这一档**不再只是日志**：数值会被标上「请求的场景未经引擎确认」（见上面 stats 的 with），
            //   界面据此显示「场景未生效」，而不是照抄设置里的「打王」——与「回了别的场景」那条硬失败
            //   态度一致：引擎没确认，界面就不许替它确认。（老 helper 把功能整个弄坏的代价我们不要，
            //   但代价应当是「如实说没确认」，不是「假装确认过了」。）
            logger.LogWarning(
                "[BuildTarget] the engine helper did not report the evaluation context it applied (requested {Context}) — it is probably an older copy, so the numbers are the build's own config",
                context);
        }

        // ⚠ 场景「有没有真正生效」必须随数值一起带出去（老 helper 没回 context 时数值就是 BD 原样）：
        //   只写一条 log 等于界面上继续照着设置里的标签说「打王」——那是假话（见 S1）。
        //   BUILD 请求按 BD 原样算，本来就不需要 helper 确认（effective 缺失即「按 BD 自己的配置」）。
        var stats = ApplyContext(ReadStats(response), context, effective);

        baselineCache.Store(template.Id, generation, context, stats);
        return (stats, null);
    }

    /// <summary>
    /// 读 helper 回的「实际生效的场景标签」（Lua 侧的 <c>result.context</c>，值形如 <c>BUILD</c>/<c>MAP</c>/<c>BOSS</c>）。
    /// 字段缺失 / 形状不对一律回 null =「老 helper，没告诉我们」——调用方按这个区分「没回」与「回了别的」。
    /// internal 是为了让单测直接喂一段假应答验形状。
    /// </summary>
    internal static string? ReadContext(PobEngineResponse? response) => response?.GetString("context");

    /// <summary>
    /// 把「请求的场景」与「引擎实际生效的场景」落到数值上（`RequestedContext` + `ContextConfirmed`）。
    ///
    /// ⚠ 抽成纯函数是为了**让单测能钉住接线本身**：<see cref="EnsureBaselineAsync"/> 要真引擎，
    ///   单测原先只能在测试里自己再拼一遍 `with` —— 等于把被测逻辑抄一份，映射少写一个字段照样全绿。
    ///   数值为 null（load_build 成功但应答里没有数值）时返回 null —— 不许造一个空 stats 出来。
    /// </summary>
    internal static PobStats? ApplyContext(PobStats? stats, string context, string? effective) =>
        stats is null
            ? null
            : stats with
            {
                RequestedContext = context == PobContexts.Build ? null : context,
                ContextConfirmed = ContextConfirmedByHelper(effective, context),
            };

    /// <summary>
    /// 「引擎有没有确认它把**请求的那个场景**用上了」——S1 的判据，抽成纯函数以便单测钉住。
    ///
    /// 三种形状（与 <see cref="EnsureBaselineAsync"/> 里的核对顺序一一对应）：
    ///   1. 请求 <c>BUILD</c> → **不需要确认**：BUILD 就是「按 BD 自己的配置算」，引擎回到的
    ///      就是这个语义，helper 有没有这个字段都成立；
    ///   2. helper 回了 <c>context</c> 且与请求一致（大小写 / 首尾空白容错）→ 确认了；
    ///   3. **老 helper 压根没回这个字段**（<paramref name="effective"/> 为 null）→ **没确认**：
    ///      请求的刷图 / 打王覆盖很可能压根没生效，数值是按 BD 原样算的。
    ///
    /// ⚠ 「回了别的场景」不走这里：那一档是硬失败（连数值都不给），见调用点。
    /// 这里只回答「这份数值该不该被当成请求场景的数据」。
    /// </summary>
    internal static bool ContextConfirmedByHelper(string? effective, string? requested) =>
        PobContexts.Normalize(requested) == PobContexts.Build
        || (effective != null
            && string.Equals(PobContexts.Normalize(effective), PobContexts.Normalize(requested), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从 helper 的应答里读数值。**形状是嵌套的**：<c>{"stats":{"dps":…,"ehp":…,"life":…}}</c> ——
    /// 一开始按顶层读，结果 load_build 明明成功却拿到 null、界面显示「引擎出错：-」（本次实测踩到）。
    ///
    /// <c>combinedDps</c> / <c>fullDps</c>（主指标回退链要用的两个备选）缺字段时给 0，
    /// 与 <c>life</c> 一样容错 —— 老 helper 没这两个字段时**不该让整份 stats 变成 null**。
    /// internal 是为了让单测直接喂一段假应答验形状。
    /// </summary>
    internal static PobStats? ReadStats(PobEngineResponse response)
    {
        var stats = response.GetObject("stats");
        if (stats == null)
        {
            return null;
        }

        double? Number(string key) =>
            stats.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                ? number
                : null;

        return Number("dps") is { } dps && Number("ehp") is { } ehp
            ? new PobStats(dps, ehp, Number("life") ?? 0)
            {
                // 回退链要用的两个字段：老 helper 没这两个字段时给 0（=「没有这个数」），
                // 与 life 一样容错 —— 缺字段绝不能让整份 stats 变成 null。
                CombinedDps = Number("combinedDps") ?? 0,
                FullDps = Number("fullDps") ?? 0,
                // 抗性上限守门要用的七项。⚠ 与上面两个**不一样**：缺字段给的是 **null（不知道）**，
                // 不是 0 —— 0 会被守门读成「这项抗性就是 0 / 没有溢出」，那是编出来的事实。
                // 老 helper 不报这几个键时，守门就当「不知道」（见 ResistanceGuardrail.Worsened）。
                FireResist = Number("fireResist"),
                ColdResist = Number("coldResist"),
                LightningResist = Number("lightningResist"),
                ChaosResist = Number("chaosResist"),
                FireResistOver = Number("fireResistOver"),
                ColdResistOver = Number("coldResistOver"),
                LightningResistOver = Number("lightningResistOver"),
            }
            : null;
    }
}

/// <summary>
/// <see cref="PobCompareService.MeasureTextAsync"/> 的结果：**没有 <see cref="Stats"/> 就没有数字**，
/// 界面与调用方一律按状态如实说，不许拿 0 当「没变化」（那是两种完全不同的结论）。
/// </summary>
public sealed class PobMeasure
{
    public PobCompareStatus Status { get; init; }

    public PobStats? Baseline { get; init; }

    public PobStats? Stats { get; init; }

    public string? Error { get; init; }

    /// <summary>引擎自己不支持的词缀行（见 <see cref="PobCompareResult.EngineUnsupportedLines"/>）。</summary>
    public IReadOnlyList<string> EngineUnsupportedLines { get; init; } = [];

    /// <summary>这次用的伤害指标（见 <see cref="PobPrimaryMetric"/>）；空串 = 算不出伤害。</summary>
    public string PrimaryMetricKey { get; init; } = "";

    /// <summary>引擎算不出这份 BD 的伤害（见 <see cref="PobCompareResult.DpsUnavailable"/>）。</summary>
    public bool DpsUnavailable { get; init; }

    public bool Ok => Stats != null;

    public static PobMeasure Succeeded(
        PobStats baseline,
        PobStats stats,
        IReadOnlyList<string>? engineUnsupported = null)
    {
        // 与 PobCompareResult.Ok 同一条纪律：**只用基线那份选指标**，不能一处基线一处候选。
        var metric = PobPrimaryMetric.Select(baseline);

        return new()
        {
            Status = PobCompareStatus.Success,
            Baseline = baseline,
            Stats = stats,
            EngineUnsupportedLines = engineUnsupported ?? [],
            PrimaryMetricKey = metric.Key,
            DpsUnavailable = !metric.Resolved,
        };
    }

    public static PobMeasure Not(
        PobCompareStatus status,
        string? error = null,
        IReadOnlyList<string>? engineUnsupported = null) =>
        new()
        {
            Status = status,
            Error = error,
            EngineUnsupportedLines = engineUnsupported ?? [],
        };
}
