namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// C2b：一件装备上**单条词缀**的收益。
///
/// 口径（**必须让用户看见，别让他自己猜**）：「把这条词缀从装备上拿掉、重算一次」得到的差值 ——
/// 也就是这条词缀**当前值多少**。
/// 它**不是**「把这条词缀洗到顶档还能涨多少」（那是另一个口径：同族顶档数值替换，数字不一样）。
///
/// ⚠ 还有一条必须写进界面的数学事实：**各条的差值不可相加**。
/// `increased` 同区相加、`more` 独立乘算，边际收益递减 ——
/// 几条词缀的差值之和 ≠ 整件装备相对基准的总差值。给「相加」的错觉等于给错结论。
/// </summary>
public sealed class AffixGainRow
{
    /// <summary>送进引擎的词缀行原文（已经是英文）。</summary>
    public required string Text { get; init; }

    /// <summary>这条是隐式词缀（拿掉它时 `Implicits: N` 要跟着减一）。</summary>
    public bool Implicit { get; init; }

    public PobCompareStatus Status { get; init; }

    /// <summary>这条词缀贡献的 DPS（= 原物 DPS − 拿掉它之后的 DPS）。</summary>
    public double DpsDelta { get; init; }

    public double EhpDelta { get; init; }

    /// <summary>相对**原物**的百分比（拿不到基准时为 null，不给假数字）。</summary>
    public double? DpsPercent { get; init; }

    public double? EhpPercent { get; init; }

    public string? Error { get; init; }

    public bool IsRanked => Status == PobCompareStatus.Success;

    public double Value(CandidateRankMetric metric) =>
        metric == CandidateRankMetric.Ehp ? EhpDelta : DpsDelta;

    public double? Percent(CandidateRankMetric metric) =>
        metric == CandidateRankMetric.Ehp ? EhpPercent : DpsPercent;
}

/// <summary>
/// 一件装备上全部词缀的收益排序结果。
///
/// 规则（<see cref="Rank"/> 是纯函数，可直接单测）：
///   1. 算出来的按选定指标降序排；同指标并列时看另一个指标；其余保持输入顺序；
///   2. **只公布前 <see cref="Count"/> 名**（用户要的就是 top5），但
///   3. **没算出来的那几条照样带出来**（<see cref="NotRanked"/>）—— 界面上要如实列一句原因，
///      直接从表里消失会让用户以为「这条词缀不存在」或「它的收益是 0」。
/// </summary>
public sealed class AffixGainRanking
{
    public CandidateRankMetric Metric { get; init; } = CandidateRankMetric.Dps;

    /// <summary>公布前几名。</summary>
    public int Count { get; init; }

    /// <summary>全部词缀行（已排序：算出来的在前）。</summary>
    public IReadOnlyList<AffixGainRow> Rows { get; init; } = [];

    public IReadOnlyList<AffixGainRow> Top { get; init; } = [];

    public IReadOnlyList<AffixGainRow> NotRanked { get; init; } = [];

    /// <summary>这批实验花掉的毫秒数（引擎热的时候约 15 ms/条）。</summary>
    public long ElapsedMs { get; init; }

    public static AffixGainRanking Rank(
        IEnumerable<AffixGainRow> rows,
        CandidateRankMetric metric,
        int count,
        long elapsedMs = 0)
    {
        var other = metric == CandidateRankMetric.Dps ? CandidateRankMetric.Ehp : CandidateRankMetric.Dps;

        var sorted = rows
            .OrderByDescending(x => x.IsRanked)
            .ThenByDescending(x => x.Value(metric))
            .ThenByDescending(x => x.Value(other))
            .ToList();

        return new AffixGainRanking
        {
            Metric = metric,
            Count = count,
            Rows = sorted,
            Top = [.. sorted.Where(x => x.IsRanked).Take(count)],
            NotRanked = [.. sorted.Where(x => !x.IsRanked)],
            ElapsedMs = elapsedMs,
        };
    }
}
