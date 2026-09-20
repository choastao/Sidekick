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
///   2. **引擎不认识的词缀条数要带出来**：PoB 只算它支持的修饰词，漏掉的会让结论偏乐观。
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

    /// <summary>「引擎里已载入哪份 BD」的缓存（键 = 模板 id + 引擎代际号，见 <see cref="BaselineCache"/>）。</summary>
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
            logger.LogInformation(
                "[BuildTarget] PoB compare {Slot}: dps {BaseDps:0} → {Dps:0}, ehp {BaseEhp:0} → {Ehp:0} ({Elapsed:0} ms, unmapped {Unmapped}, annotation-stripped {Annotated})",
                pobSlot,
                measured.Baseline!.Dps,
                current.Dps,
                measured.Baseline.Ehp,
                current.Ehp,
                stopwatch.ElapsedMilliseconds,
                conversion.Result!.Skipped,
                conversion.Result.AnnotationStripped);

            return PobCompareResult.Ok(measured.Baseline, current, conversion.Result.Skipped);
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
                ? PobMeasure.Succeeded(measured.Baseline!, stats)
                : PobMeasure.Not(measured.Status, measured.Error);
        }
        catch (OperationCanceledException)
        {
            throw;   // 调用方取消不是「引擎出错」，交给上层（C2a 的批量取消也走这条）
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] PoB measure failed");
            return PobMeasure.Not(PobCompareStatus.Failed, ex.Message);
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
    private async Task<(PobCompareStatus Status, PobStats? Baseline, PobStats? Stats, string? Error)> EquipAndMeasureAsync(
        BuildTargetTemplate template,
        string pobSlot,
        string itemText,
        CancellationToken cancellationToken)
    {
        if (!await engine.EnsureStartedAsync(cancellationToken))
        {
            return (PobCompareStatus.EngineUnavailable, null, null, engine.LastError);
        }

        var baseline = await EnsureBaselineAsync(template, cancellationToken);
        if (baseline == null)
        {
            return (PobCompareStatus.Failed, null, null, engine.LastError);
        }

        var response = await EquipAsync(pobSlot, itemText, cancellationToken);

        // 引擎被换代过（例如恰好赶在一次超时重启之后）：Lua 侧会回 "no build loaded"。
        // 兜底重载一次再试 —— 正常路径靠 BaselineCache 的代际号就拦住了，这是最后一道保险
        // （审计 L1：原先这条路径完全没有自愈，一次超时之后每次试穿都永久失败）。
        if (response is not { Ok: true } && IsNoBuildLoaded(response))
        {
            logger.LogWarning("[BuildTarget] Engine reports no build loaded, reloading the baseline once");
            baselineCache.Invalidate();

            baseline = await EnsureBaselineAsync(template, cancellationToken);
            if (baseline == null)
            {
                return (PobCompareStatus.Failed, null, null, engine.LastError);
            }

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
                return (PobCompareStatus.BaseUnknown, null, null, null);
            }

            // 失败时把转换出来的文本一起记下来：没有它，下次定位要重跑一遍真机
            logger.LogWarning(
                "[BuildTarget] PoB equip failed: {Error}. Item text:\n{Text}",
                response.Error ?? engine.LastError,
                itemText);
            return (PobCompareStatus.Failed, baseline, null, response?.Error ?? engine.LastError);
        }

        var current = ReadStats(response);
        if (current == null)
        {
            return (PobCompareStatus.Failed, baseline, null, "engine returned no stats");
        }

        return (PobCompareStatus.Success, baseline, current, null);
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

    /// <summary>载入基准 BD。同一个模板 + 同一个引擎代次只载一次（引擎侧 load_build 约 400 ms）。</summary>
    private async Task<PobStats?> EnsureBaselineAsync(BuildTargetTemplate template, CancellationToken cancellationToken)
    {
        if (baselineCache.IsValid(template.Id, engine.Generation))
        {
            return baselineCache.Stats;
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
            },
            TimeSpan.FromSeconds(90),   // 首次要加载引擎数据，给足时间
            cancellationToken);

        if (response is not { Ok: true })
        {
            baselineCache.Invalidate();
            return null;
        }

        var stats = ReadStats(response);
        baselineCache.Store(template.Id, generation, stats);
        return stats;
    }

    /// <summary>
    /// 从 helper 的应答里读数值。**形状是嵌套的**：<c>{"stats":{"dps":…,"ehp":…,"life":…}}</c> ——
    /// 一开始按顶层读，结果 load_build 明明成功却拿到 null、界面显示「引擎出错：-」（本次实测踩到）。
    /// </summary>
    private static PobStats? ReadStats(PobEngineResponse response)
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

    public bool Ok => Stats != null;

    public static PobMeasure Succeeded(PobStats baseline, PobStats stats) =>
        new() { Status = PobCompareStatus.Success, Baseline = baseline, Stats = stats };

    public static PobMeasure Not(PobCompareStatus status, string? error = null) =>
        new() { Status = status, Error = error };
}
