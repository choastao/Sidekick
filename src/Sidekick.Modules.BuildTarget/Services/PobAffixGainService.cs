using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// C2b：算出这件装备上**每条词缀各自值多少**，按收益排序给前 5 条。
///
/// 口径：**把这条词缀从装备上拿掉、用引擎重算一次**，与原物求差 —— 即这条词缀当前贡献了多少。
/// （另一个口径「洗到同族顶档能涨多少」要用到逐档数值表，本版不做，别把这两个数混为一谈。）
///
/// 成本：1 次原物试穿 + N 次变体试穿，热引擎 ~15 ms/次 → 8 条词缀约 150 ms。
/// 复用 <see cref="PobCompareService.MeasureTextAsync"/>（同一把闸、同一份基线缓存），
/// **不另写一条「每次都从头算」的路径**。
///
/// 三条纪律：
///   1. **拿不到原物数值就整块不算**：没有参照系就没有差值，宁可不显示，也不给「0 变化」这种假结论；
///   2. **引擎级失败立刻停**（没装/崩/超时），**已算出来的保留**；
///   3. **定位不到那一行时如实标出来**，不去别处找一条「看起来像」的行来删。
/// </summary>
public class PobAffixGainService(
    PobCompareService compare,
    PobItemTextService itemText,
    BuildTargetOptionsStore options,
    ILogger<PobAffixGainService> logger)
{
    public const int DefaultCount = 5;

    /// <summary>
    /// 返回 null = **这一栏不该显示**（开关没开、没模板、没 BD 源码、珠宝、槽位不支持、
    /// 基底取不到英文、连原物都试穿不了）—— 这些情况「试穿对比」那一栏已经如实说明原因，
    /// 这里再重复一遍只会变成两处文案要同步。
    /// </summary>
    public async Task<AffixGainRanking?> RankAsync(
        BuildTargetTemplate? template,
        Item? item,
        string? slotKey,
        CandidateRankMetric metric = CandidateRankMetric.Dps,
        int count = DefaultCount,
        CancellationToken cancellationToken = default)
    {
        if (!options.PobEngine || template == null || string.IsNullOrWhiteSpace(template.PobXml))
        {
            return null;
        }

        var pobSlot = PobCompareService.MapSlot(slotKey);
        if (pobSlot == null)
        {
            return null;
        }

        var text = await itemText.BuildAsync(item);
        if (text is not { BaseIdentified: true } || text.Affixes.Count == 0)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();

        // 参照系：把**原物**试穿一次（~15 ms）。拿不到就整块不算。
        var reference = await compare.MeasureTextAsync(template, pobSlot, text.Text, cancellationToken);
        if (!reference.Ok)
        {
            logger.LogInformation(
                "[BuildTarget] Affix gains skipped: the item itself could not be measured ({Status})",
                reference.Status);
            return null;
        }

        var referenceStats = reference.Stats!;
        var rows = new List<AffixGainRow>(text.Affixes.Count);
        var engineDead = false;

        // 真实发出的试穿次数（含原物那次）——**别拿「词缀行数」当次数**：
        // 定位失败的行、引擎死掉后补齐的行都压根没试穿过，拿行数会在界面上报一个虚高的数。
        var attempts = 1;

        foreach (var affix in text.Affixes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (engineDead)
            {
                rows.Add(Row(affix, PobCompareStatus.EngineUnavailable));
                continue;
            }

            var variant = PobItemText.WithoutAffix(text.Text, affix);
            if (variant == null)
            {
                // 行号/行数对不上 = 我们自己造的文本有问题（**不是引擎出错**，引擎压根没被调用）。
                // 如实标成转换层缺陷，别甩到引擎头上。
                logger.LogWarning("[BuildTarget] Affix gain: could not locate the affix line '{Line}'", affix.Text);
                rows.Add(Row(affix, PobCompareStatus.AffixLost));
                continue;
            }

            attempts++;
            var measured = await compare.MeasureTextAsync(template, pobSlot, variant, cancellationToken);
            if (!measured.Ok)
            {
                rows.Add(Row(affix, measured.Status, measured.Error));

                if (measured.Status == PobCompareStatus.EngineUnavailable)
                {
                    // 同一个引擎上后面的必然同样失败 —— 不再逐个白试，但已算出来的保留
                    engineDead = true;
                    logger.LogWarning(
                        "[BuildTarget] Affix gains stopped early: engine unavailable ({Error})",
                        measured.Error);
                }

                continue;
            }

            var dpsDelta = referenceStats.Dps - measured.Stats!.Dps;
            var ehpDelta = referenceStats.Ehp - measured.Stats.Ehp;

            rows.Add(Row(
                affix,
                PobCompareStatus.Success,
                dpsDelta: dpsDelta,
                ehpDelta: ehpDelta,
                dpsPercent: referenceStats.Dps > 0 ? dpsDelta / referenceStats.Dps * 100 : null,
                ehpPercent: referenceStats.Ehp > 0 ? ehpDelta / referenceStats.Ehp * 100 : null));
        }

        stopwatch.Stop();

        var ranking = AffixGainRanking.Rank(rows, metric, count, stopwatch.ElapsedMilliseconds, attempts);

        logger.LogInformation(
            "[BuildTarget] Affix gains: {Ranked}/{Total} ranked by {Metric} in {Elapsed} ms",
            ranking.Top.Count,
            rows.Count,
            metric,
            stopwatch.ElapsedMilliseconds);

        return ranking;
    }

    private static AffixGainRow Row(
        PobItemText.AffixLine affix,
        PobCompareStatus status,
        string? error = null,
        double dpsDelta = 0,
        double ehpDelta = 0,
        double? dpsPercent = null,
        double? ehpPercent = null) => new()
    {
        Text = affix.Text,
        Implicit = affix.Implicit,
        Status = status,
        DpsDelta = dpsDelta,
        EhpDelta = ehpDelta,
        DpsPercent = dpsPercent,
        EhpPercent = ehpPercent,
        Error = error,
    };
}
