namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>候选排序的排序指标。</summary>
public enum CandidateRankMetric
{
    Dps,
    Ehp,
}

/// <summary>
/// 一件候选试穿后的结果（C2a 的排序行）。
///
/// **只有 <see cref="IsRanked"/> 的行才有数字**：没算出来的行照样列出来（并带一句原因），
/// 而不是从表里消失 —— 少一行会让用户以为「篮子里的东西没被算过」。
/// </summary>
public sealed class CandidateRankRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string BaseType { get; init; } = string.Empty;

    public string SlotKey { get; init; } = string.Empty;

    public PobCompareStatus Status { get; init; }

    public double DpsDelta { get; init; }

    public double EhpDelta { get; init; }

    public double? DpsPercent { get; init; }

    public double? EhpPercent { get; init; }

    /// <summary>**我们转换层**没认出的词缀条数（>0 时这几条没参与计算）。</summary>
    public int UnmappedAffixes { get; init; }

    /// <summary>引擎自己不支持的词缀行（见 <see cref="PobCompareResult.EngineUnsupportedLines"/>）。</summary>
    public IReadOnlyList<string> EngineUnsupportedLines { get; init; } = [];

    /// <summary>这一行对比时用的伤害指标（见 <see cref="PobCompareResult.PrimaryMetricKey"/>）。</summary>
    public string PrimaryMetricKey { get; init; } = "";

    /// <summary>
    /// 引擎算不出这份 BD 的伤害：这行的 DPS 差值必然是 0 ——
    /// 界面显示「—」而不是 0（0 会被读成「这件没变化」）。
    /// </summary>
    public bool DpsUnavailable { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// **引擎指标那条轴**的判定（相对当前装备，见 <see cref="ItemVerdictDecider"/>）。
    /// 没算出来的行是 <see cref="ItemVerdict.Unresolved"/> —— 界面不给它们挂标签。
    /// </summary>
    public ItemVerdict Verdict { get; init; } = ItemVerdict.Unresolved;

    /// <summary><see cref="Verdict"/> 对应的理由键（资源名，见 <see cref="ItemVerdictDecider"/>）。</summary>
    public string VerdictReasonKey { get; init; } = "";

    /// <summary>相对**当前装备**的帕累托关系（见 <see cref="Pareto.Compare"/>）。</summary>
    public ParetoStatus Pareto { get; init; } = ParetoStatus.Unknown;

    /// <summary>
    /// 这一行是**在什么前提下**算出来的（模板 + 场景 + 主指标 + 引擎代际）。
    /// 只有算出来的行才有值；界面拿它判「前提已变」（见 <see cref="BasketComparability"/>）。
    ///
    /// ⚠ 可写：一行是先造出来、前提（主指标要等这一批算完才知道）后补的
    /// （见 <c>PobCandidateRanker</c>）。除此之外不许改。
    /// </summary>
    public BasketPremise? Premise { get; set; }

    /// <summary>算出来了（有差值），参与排序。</summary>
    public bool IsRanked => Status == PobCompareStatus.Success;

    /// <summary>该行在给定指标下的排序值。</summary>
    public double Value(CandidateRankMetric metric) =>
        metric == CandidateRankMetric.Ehp ? EhpDelta : DpsDelta;
}

/// <summary>
/// 候选排序的结果集。
///
/// 排序规则（<see cref="Sort"/>，纯函数，可直接单测）：
/// 1. **算出来的排在前面**，按选定指标降序；
/// 2. 同指标并列时，另一个指标高者优先（免得「DPS 一样、EHP 差很多」的两件看起来随机）；
/// 3. 其余并列保持输入顺序（LINQ 的 OrderBy 是稳定排序）；
/// 4. 没算出来的行按输入顺序排在最后 —— 它们在界面上照样显示，只是没有名次。
/// </summary>
public sealed class CandidateRanking
{
    public CandidateRankMetric Metric { get; init; } = CandidateRankMetric.Dps;

    public IReadOnlyList<CandidateRankRow> Rows { get; init; } = [];

    public IReadOnlyList<CandidateRankRow> Ranked => [.. Rows.Where(x => x.IsRanked)];

    public IReadOnlyList<CandidateRankRow> NotRanked => [.. Rows.Where(x => !x.IsRanked)];

    /// <summary>第一名（没有算出来的行时为 null）。</summary>
    public CandidateRankRow? Best => Ranked.FirstOrDefault();

    public static CandidateRanking Sort(IEnumerable<CandidateRankRow> rows, CandidateRankMetric metric)
    {
        var other = metric == CandidateRankMetric.Dps ? CandidateRankMetric.Ehp : CandidateRankMetric.Dps;

        var sorted = rows
            .OrderByDescending(x => x.IsRanked)
            .ThenByDescending(x => x.Value(metric))
            .ThenByDescending(x => x.Value(other))
            .ToList();

        return new CandidateRanking { Metric = metric, Rows = sorted };
    }
}
