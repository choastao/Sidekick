namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「这次用什么数当伤害」——**主指标的回退链**（纯逻辑，单测直接调）。
///
/// 为什么需要它：PoB 的 <c>TotalDPS</c> 是**主技能**的 DPS，某些 BD（召唤 / 触发类）引擎
/// 给不出这个值（为 0），而 <c>CombinedDPS</c> / <c>FullDPS</c> 是有数的。此时界面上显示 0
/// 会被读成「这件装备没贡献」—— 结论完全错。回退链说的是「换个字段取数」，**不是**换算法：
/// 引擎怎么算的、配置怎么配的，一概不变。
///
/// 顺序（**必须按顺序判，且每档都要求大于 0**）：
///   1. <c>TotalDPS</c> —— **默认路径，绝不能变**：我们的基准数字、文档与真机基线的对账都建立在它上面；
///   2. <c>CombinedDPS</c>；
///   3. <c>FullDPS</c>；
///   4. 都没有（全为 0 或负）→ 回 <c>("", 0, false)</c>：**引擎算不出这份 BD 的伤害**，
///      界面必须如实说「算不出来」，不许退化成 0 当数字用。
///
/// ⚠ 负值一律当「没有」：PoB 个别字段在配置不成立时会给负数，
///   拿它当基准会让「涨了多少」的符号整个反过来。
/// </summary>
public static class PobPrimaryMetric
{
    /// <summary>PoB 的 TotalDPS（主技能）—— 默认主指标。</summary>
    public const string TotalDps = "TotalDPS";

    /// <summary>PoB 的 CombinedDPS（主技能 + 其它技能）。</summary>
    public const string CombinedDps = "CombinedDPS";

    /// <summary>PoB 的 FullDPS（全部技能）。</summary>
    public const string FullDps = "FullDPS";

    /// <summary>
    /// 按回退链选出这次要用的伤害指标。<c>Resolved</c> 为 false 时 Key 是空串、Value 是 0 ——
    /// 调用方**按 Resolved 判**，不要去比 Value 是不是 0（那正是本类要消灭的那种含糊）。
    /// </summary>
    public static (string Key, double Value, bool Resolved) Select(PobStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.Dps > 0)
        {
            return (TotalDps, stats.Dps, true);
        }

        if (stats.CombinedDps > 0)
        {
            return (CombinedDps, stats.CombinedDps, true);
        }

        if (stats.FullDps > 0)
        {
            return (FullDps, stats.FullDps, true);
        }

        return ("", 0, false);
    }

    /// <summary>
    /// 指标的界面名（<c>Pob_Primary_Metric</c> 的 {0}）。
    /// 认不出的键（含空串）回空串 —— 界面据此不显示那一行，而不是显示一个空括号。
    /// </summary>
    public static string DisplayName(string? key) => key switch
    {
        TotalDps => "主技能 DPS",
        CombinedDps => "综合 DPS",
        FullDps => "全技能 DPS",
        _ => "",
    };
}
