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
/// </summary>
public class PobCompareService(
    PobEngineClient engine,
    PobItemTextService itemText,
    BuildTargetOptionsStore options,
    ILogger<PobCompareService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private string? loadedTemplateId;
    private PobStats? loadedBaseStats;

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
    };

    public static string? MapSlot(string? slotKey) =>
        slotKey != null && PobSlotNames.TryGetValue(slotKey, out var name) ? name : null;

    public async Task<PobCompareResult> CompareAsync(
        BuildTargetTemplate? template,
        Item? item,
        string? slotKey,
        CancellationToken cancellationToken = default)
    {
        if (!options.PobEngine)
        {
            return PobCompareResult.Not(PobCompareStatus.Disabled);
        }

        if (template == null || item == null)
        {
            return PobCompareResult.Not(PobCompareStatus.NoTemplate);
        }

        if (string.IsNullOrWhiteSpace(template.PobXml))
        {
            return PobCompareResult.Not(PobCompareStatus.NoBuildXml);
        }

        var pobSlot = MapSlot(slotKey);
        if (pobSlot == null)
        {
            return PobCompareResult.Not(PobCompareStatus.UnsupportedSlot);
        }

        var stopwatch = Stopwatch.StartNew();

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await engine.EnsureStartedAsync(cancellationToken))
            {
                return PobCompareResult.Not(PobCompareStatus.EngineUnavailable, engine.LastError);
            }

            var baseline = await EnsureBaselineAsync(template, cancellationToken);
            if (baseline == null)
            {
                return PobCompareResult.Not(PobCompareStatus.Failed, engine.LastError);
            }

            var text = await itemText.BuildAsync(item);
            if (text == null)
            {
                return PobCompareResult.Not(PobCompareStatus.ConversionFailed);
            }

            if (!text.BaseIdentified)
            {
                // PoB 认不出中文基底（它内部会拿 baseName 拼接然后炸），这里提前拦住
                return PobCompareResult.Not(PobCompareStatus.BaseUnknown);
            }

            var response = await engine.SendAsync(
                PobEngineProtocol.MethodEquip,
                new Dictionary<string, object?>
                {
                    ["slot"] = pobSlot,
                    ["item"] = text.Text,
                },
                TimeSpan.FromSeconds(15),
                cancellationToken);

            if (response is not { Ok: true })
            {
                return PobCompareResult.Not(PobCompareStatus.Failed, response?.Error ?? engine.LastError);
            }

            var current = ReadStats(response);
            if (current == null)
            {
                return PobCompareResult.Not(PobCompareStatus.Failed, "engine returned no stats");
            }

            stopwatch.Stop();
            logger.LogInformation(
                "[BuildTarget] PoB compare {Slot}: dps {BaseDps:0} → {Dps:0}, ehp {BaseEhp:0} → {Ehp:0} ({Elapsed:0} ms, unmapped {Unmapped})",
                pobSlot,
                baseline.Dps,
                current.Dps,
                baseline.Ehp,
                current.Ehp,
                stopwatch.ElapsedMilliseconds,
                text.Skipped);

            return PobCompareResult.Ok(baseline, current, text.Skipped);
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

    /// <summary>载入基准 BD。同一个模板只载一次（引擎侧 load_build 约 400 ms，不必每次重来）。</summary>
    private async Task<PobStats?> EnsureBaselineAsync(BuildTargetTemplate template, CancellationToken cancellationToken)
    {
        if (loadedTemplateId == template.Id && loadedBaseStats != null)
        {
            return loadedBaseStats;
        }

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
            loadedTemplateId = null;
            loadedBaseStats = null;
            return null;
        }

        loadedTemplateId = template.Id;
        loadedBaseStats = ReadStats(response);
        return loadedBaseStats;
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
