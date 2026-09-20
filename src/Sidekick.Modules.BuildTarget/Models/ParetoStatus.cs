namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 一件候选**相对当前装备**在两个轴上的支配关系（见 <see cref="Pareto.Compare"/>）。
/// </summary>
public enum ParetoStatus
{
    /// <summary>两维都不差、至少一维更好。</summary>
    Dominates,

    /// <summary>一维更好、一维更差（两维都有数时才会是这一档）。</summary>
    Tradeoff,

    /// <summary>两维都不优（都更差或持平）。**我们比 ExileLens 多出来的一档**。</summary>
    Dominated,

    /// <summary>两维都一样（都在容差内）。</summary>
    Equal,

    /// <summary>缺一维（引擎算不出那个数）→ **无从判断支配关系**，不许拿「缺数」冒充「一维更好一维更差」。</summary>
    Unknown,
}

/// <summary>
/// 候选相对**当前装备**的帕累托支配判定（纯逻辑，单测直接调）。
///
/// 语义对应 ExileLens 的 <c>DOMINATES</c> / <c>TRADEOFF</c> / <c>NEITHER_DOMINATES</c>，
/// 我们在它基础上**多一个 <see cref="ParetoStatus.Dominated"/>**：
/// 它把「两维都更差」并进 <c>NEITHER_DOMINATES</c>，而那种情况在我们这边的
/// 「不建议 / 下降」语义上是独立的一档，混进去会让用户看不出方向。
///
/// 容差 <see cref="TolerancePercent"/>：±0.5% 以内的波动当成「一样」——
/// 引擎重算有浮点噪声，拿 ±0.0001% 判「更好」是噪声当信号。
///
/// 某一维为 <c>null</c>（引擎算不出那个数）= **无从判断支配关系**，
/// 落到 <see cref="ParetoStatus.Unknown"/>（文案是「判不了」）—— **不是** <see cref="ParetoStatus.Tradeoff"/>：
/// Tradeoff 说的是「一维更好一维更差」，那是拿缺数当结论；Unknown 才是如实说「判不了」。
/// （类注释原先写的是 Tradeoff，与代码相反，本批按代码改正。）
/// </summary>
public static class Pareto
{
    /// <summary>容差（%）：两维都在这个范围内算「一样」。</summary>
    public const double TolerancePercent = 0.5;

    /// <summary>
    /// 候选相对当前装备的支配关系。
    /// <list type="bullet">
    /// <item>两维都 ≥ -0.5% 且至少一维 ≥ +0.5% → <see cref="ParetoStatus.Dominates"/></item>
    /// <item>两维都 ≤ +0.5% 且至少一维 ≤ -0.5% → <see cref="ParetoStatus.Dominated"/></item>
    /// <item>两维都在 ±0.5% 内 → <see cref="ParetoStatus.Equal"/></item>
    /// <item>其余（两维都有数但方向相反）→ <see cref="ParetoStatus.Tradeoff"/>；任一维为 <c>null</c> → <see cref="ParetoStatus.Unknown"/></item>
    /// </list>
    /// </summary>
    /// <param name="dpsDeltaPercent">伤害相对当前装备的变化（%）；<c>null</c> = 算不出。</param>
    /// <param name="ehpDeltaPercent">EHP 相对当前装备的变化（%）；<c>null</c> = 算不出。</param>
    public static ParetoStatus Compare(double? dpsDeltaPercent, double? ehpDeltaPercent)
    {
        // 缺一维 = 无从判断支配关系 → Unknown（**不是** Tradeoff：那会说成「一维更好一维更差」）。
        // ⚠ 这条必须**先**判：C# 里 null 参与的大小比较恒为 false，
        //   掉进下面的分支会「看着像不满足任何一条」然后被最后一行兜底，语义上就说不清了。
        if (dpsDeltaPercent is not { } dps || ehpDeltaPercent is not { } ehp)
        {
            return ParetoStatus.Unknown;
        }

        const double t = TolerancePercent;

        // 支配在前：两维都不差 + 至少一维更好。
        if (dps >= -t && ehp >= -t && (dps >= t || ehp >= t))
        {
            return ParetoStatus.Dominates;
        }

        // 被支配：两维都不优 + 至少一维更差。
        if (dps <= t && ehp <= t && (dps <= -t || ehp <= -t))
        {
            return ParetoStatus.Dominated;
        }

        // 两维都在容差内 = 一样。
        if (Math.Abs(dps) <= t && Math.Abs(ehp) <= t)
        {
            return ParetoStatus.Equal;
        }

        return ParetoStatus.Tradeoff;
    }
}
