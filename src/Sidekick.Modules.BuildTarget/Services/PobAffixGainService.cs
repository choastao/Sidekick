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

        // ⚠ 引擎算不出伤害时（DpsUnavailable）**按 EHP 排名**：否则排的是「一列 0」，
        //   名次完全由并列规则（另一个指标）决定 —— 表上却写着「按 DPS 排序」，是假话。
        //   这里只换排序指标，逐条的 EHP 收益本来就都算着（见下面 Row 的 ehpDelta）。
        metric = EffectiveMetric(reference.DpsUnavailable, metric);

        // 引擎自己不支持的词缀行（拿原物那次试穿的结果读回来的）——
        // 这些词缀的收益必然是 0，界面上要标成「引擎不支持」而不是「没贡献」。
        var unsupportedByEngine = new HashSet<string>(reference.EngineUnsupportedLines, StringComparer.OrdinalIgnoreCase);

        // ⚠ 一条词缀可能占**多个物理行**（英文模板里就有内嵌换行），而 helper 回的是 PoB 侧的
        //   单行/多行原文。只比整串会失配 → 那条盲区词缀又会显示成裸的 +0（少标一档）。
        //   所以整串与它的每一行都要比（Trim 过，免得行首空格对不上）。
        bool EngineUnsupported(string text) =>
            unsupportedByEngine.Contains(text) ||
            text.Split('\n').Any(line => unsupportedByEngine.Contains(line.Trim()));

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
                rows.Add(Row(affix, PobCompareStatus.EngineUnavailable, unsupportedByEngine: EngineUnsupported(affix.Text)));
                continue;
            }

            var variant = PobItemText.WithoutAffix(text.Text, affix);
            if (variant == null)
            {
                // 行号/行数对不上 = 我们自己造的文本有问题（**不是引擎出错**，引擎压根没被调用）。
                // 如实标成转换层缺陷，别甩到引擎头上。
                logger.LogWarning("[BuildTarget] Affix gain: could not locate the affix line '{Line}'", affix.Text);
                rows.Add(Row(affix, PobCompareStatus.AffixLost, unsupportedByEngine: EngineUnsupported(affix.Text)));
                continue;
            }

            var measured = await compare.MeasureTextAsync(template, pobSlot, variant, cancellationToken);

            // 只在**真的走到了引擎**时才计数（审计第二轮 3-2）：`Disabled / NoTemplate / NoBuildXml`
            // 是纯早退（例如运行中把引擎开关关掉），压根没有试穿，算进去会让「共试穿 N 次」虚高。
            if (measured.Status is not (PobCompareStatus.Disabled or PobCompareStatus.NoTemplate or PobCompareStatus.NoBuildXml))
            {
                attempts++;
            }

            if (!measured.Ok)
            {
                rows.Add(Row(affix, measured.Status, measured.Error, unsupportedByEngine: EngineUnsupported(affix.Text)));

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
                unsupportedByEngine: EngineUnsupported(affix.Text),
                dpsPercent: referenceStats.Dps > 0 ? dpsDelta / referenceStats.Dps * 100 : null,
                ehpPercent: referenceStats.Ehp > 0 ? ehpDelta / referenceStats.Ehp * 100 : null));
        }

        stopwatch.Stop();

        var ranking = AffixGainRanking.Rank(rows, metric, count, attempts, stopwatch.ElapsedMilliseconds, reference.DpsUnavailable);

        logger.LogInformation(
            "[BuildTarget] Affix gains: {Ranked}/{Total} ranked by {Metric} in {Elapsed} ms",
            ranking.Top.Count,
            rows.Count,
            metric,
            stopwatch.ElapsedMilliseconds);

        return ranking;
    }

    /// <summary>
    /// 这批实验实际按哪个指标排名：DPS 算不出来时一律改按 EHP。
    /// 抽成纯函数是为了能单测钉住（真跑一遍引擎在单测里做不到）。
    /// </summary>
    internal static CandidateRankMetric EffectiveMetric(bool dpsUnavailable, CandidateRankMetric requested) =>
        dpsUnavailable ? CandidateRankMetric.Ehp : requested;

    private static AffixGainRow Row(
        PobItemText.AffixLine affix,
        PobCompareStatus status,
        string? error = null,
        double dpsDelta = 0,
        double ehpDelta = 0,
        double? dpsPercent = null,
        double? ehpPercent = null,
        bool unsupportedByEngine = false) => new()
    {
        Text = affix.Text,
        Implicit = affix.Implicit,
        Status = status,
        UnsupportedByEngine = unsupportedByEngine,
        DpsDelta = dpsDelta,
        EhpDelta = ehpDelta,
        DpsPercent = dpsPercent,
        EhpPercent = ehpPercent,
        Error = error,
    };
}
