namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 一次「要洗出什么」的概率估算结果。
///
/// 期望次数 = 1 / P，期望成本 = 期望次数 × 单次操作成本。
/// 期望次数是**均值**（平均要操作几次），不是「几次必出」——界面文案必须写成
///「平均 1/N 次」，不能写成「N 次必出」。
/// </summary>
public sealed record ExpectedCostEstimate
{
    /// <summary>池条数（改造：能出的词缀条数；重掷：tier 区间里的取值个数）。</summary>
    public int PoolCount { get; init; }

    /// <summary>目标条数（池里能满足目标的条目数）。</summary>
    public int TargetCount { get; init; }

    /// <summary>P = 目标条数 / 池条数，取值 0-1。0 表示算不出来（池空 / 没有匹配的目标）。</summary>
    public double Probability { get; init; }

    /// <summary>单次操作成本（神聖石计价，含预兆，与面板里的单次成本同一口径）。</summary>
    public double SingleOperationCost { get; init; }

    /// <summary>能不能给出期望值：池非空、池里有目标、且 P &gt; 0。</summary>
    public bool IsUsable => PoolCount > 0 && TargetCount > 0 && Probability > 0;

    /// <summary>期望的操作次数（1 / P）。算不出来时是正无穷，调用方先看 <see cref="IsUsable"/>。</summary>
    public double ExpectedTries => IsUsable ? 1d / Probability : double.PositiveInfinity;

    /// <summary>期望成本（神聖石）。算不出来时是正无穷。</summary>
    public double ExpectedCost => IsUsable ? ExpectedTries * SingleOperationCost : double.PositiveInfinity;

    /// <summary>P 的百分数形式（界面保留两位小数）。</summary>
    public double ProbabilityPercent => Probability * 100d;
}
