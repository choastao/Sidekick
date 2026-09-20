namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「这件装备相对当前装备算什么」——**引擎指标那条轴**的结论。
///
/// ⚠ 与 <see cref="Verdict"/>（门槛 + 加法词缀那条轴，见 <c>VerdictDecider</c>）是**两回事**：
///   那条轴说的是「按你自己勾的门槛判」，这条说的是「按引擎算出来的 DPS / EHP 判」。
///   两条轴各有各的意思，界面上要能看出用的是哪一条，**不合并**。
///
/// 判定顺序与每一条的判据写在 <see cref="ItemVerdictDecider.Decide"/> 上（顺序即优先级）。
/// </summary>
public enum ItemVerdict
{
    /// <summary>信息不足（两维都拿不到数，或引擎压根算不出这份 BD）。</summary>
    Unresolved,

    /// <summary>基本没变化（两维都在 ±1% 以内）。</summary>
    NoChange,

    /// <summary>互有来回但幅度都很小。**当前判定顺序下不会产出**，留着是为了语义完整。</summary>
    Sidegrade,

    /// <summary>进攻提升、防御没降。</summary>
    OffenseUpgrade,

    /// <summary>防御提升、进攻没降。</summary>
    DefenseUpgrade,

    /// <summary>攻防双升（幅度都不大）。</summary>
    ClearUpgrade,

    /// <summary>攻防双升且幅度都大。</summary>
    StrongUpgrade,

    /// <summary>有得有失（一升一降 / 触到硬性门槛 / 顶到上限的抗性被拉低）。</summary>
    Tradeoff,

    /// <summary>下降（攻防双降，幅度不大）。</summary>
    Downgrade,

    /// <summary>大幅下降（攻防双降且幅度都大）。</summary>
    StrongDowngrade,
}

/// <summary>
/// <see cref="ItemVerdictDecider"/> 的输入。
///
/// 两个百分比都是「相对**当前装备**」的相对变化（见 <see cref="PobCompareResult.DpsPercent"/>）；
/// <c>null</c> = 这一维**引擎给不出数**（不是 0，别拿 0 当「没变化」）。
/// </summary>
/// <param name="DpsPercent">伤害指标的相对变化（%）；<c>null</c> = 算不出（<see cref="PobCompareResult.DpsUnavailable"/>）。</param>
/// <param name="EhpPercent">EHP 的相对变化（%）；<c>null</c> = 算不出（基线为 0 之类）。</param>
/// <param name="HardGateFailed">用户自己勾的硬性门槛没过（**不是**从 BD 导入的参考值）。</param>
/// <param name="ResistanceDeficitWorsened">见 <see cref="ResistanceGuardrail.Worsened"/>。</param>
/// <param name="MetricsPartial">
/// true = **只有一维有数**（现在的调用方只在这一维是 EHP 时置位：DPS 算不出来）。
/// 判定里真的读了它（见 <see cref="ItemVerdictDecider.Decide"/> 第 ⑨ 条）——
/// 原来它写进去没人读，留着会被后来人当成一个不生效的开关，所以本轮把它接上了线。
/// </param>
public sealed record ItemVerdictInput(
    double? DpsPercent,
    double? EhpPercent,
    bool HardGateFailed,
    bool ResistanceDeficitWorsened,
    bool MetricsPartial)
{
    /// <summary>
    /// 从一次试穿对比的结果装配输入。**只做搬运，不自己算判据**：
    /// 抗性守门交给 <see cref="ResistanceGuardrail"/>，硬性门槛由调用方（它才拿得到 Evaluation）传进来。
    /// </summary>
    public static ItemVerdictInput From(PobCompareResult result, bool hardGateFailed = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ItemVerdictInput(
            // DpsUnavailable = 引擎算不出伤害 → 这一维传 null（**不是**传 0）
            DpsPercent: result.DpsUnavailable ? null : result.DpsPercent,
            EhpPercent: result.EhpPercent,
            HardGateFailed: hardGateFailed,
            ResistanceDeficitWorsened: ResistanceGuardrail.Worsened(result.Base, result.Current),
            MetricsPartial: result.DpsUnavailable);
    }
}

/// <summary>
/// 引擎指标判定（**纯逻辑，单测直接调**）。
///
/// 三条纪律：
///   1. **顺序即优先级**：下面 9 条必须按写死的顺序判，不许重排、不许合并
///      （顺序换了结论就换，例如「硬门槛没过」必须压过「攻防双升」）；
///   2. **null ≠ 0**：某一维是 null 表示「引擎算不出来」，绝不参与大小比较，
///      也不许当成 0 去凑「没变化」；
///   3. **阈值**：<see cref="SmallPercent"/> 是「有意义」的下沿，
///      <see cref="LargePercent"/> 是「大」的下沿。边界值（正好 ±1% / ±5%）**算入提升 / 下降那一侧**：
///      正好 +1% 不是「没变化」，正好 +5% 已经算「大幅」。
/// </summary>
public static class ItemVerdictDecider
{
    /// <summary>「有意义」的幅度（%）。</summary>
    public const double SmallPercent = 1.0;

    /// <summary>「大」的幅度（%）。</summary>
    public const double LargePercent = 5.0;

    // 理由键（资源名）。集中定义免得各处拼字符串拼错。
    public const string ReasonHardGate = "Verdict_Reason_HardGate";
    public const string ReasonNoMetrics = "Verdict_Reason_NoMetrics";
    public const string ReasonResCap = "Verdict_Reason_ResCap";
    public const string ReasonNoChange = "Verdict_Reason_NoChange";
    public const string ReasonBothUp = "Verdict_Reason_BothUp";
    public const string ReasonOffense = "Verdict_Reason_Offense";
    public const string ReasonDefense = "Verdict_Reason_Defense";
    public const string ReasonBothDown = "Verdict_Reason_BothDown";
    public const string ReasonMixed = "Verdict_Reason_Mixed";
    public const string ReasonOnlyEhp = "Verdict_Reason_OnlyEhp";

    /// <summary>
    /// 只有 DPS 有数（EHP 这一维引擎给不出来）时用的理由键。
    /// ⚠ 规格给的那张表里只有 <see cref="ReasonOnlyEhp"/>（对称的另一半没写）。
    /// 这里补一条对称的键，见报告「不确定的地方」。
    /// </summary>
    public const string ReasonOnlyDps = "Verdict_Reason_OnlyDps";
    /// <summary>只有进攻这一维下降（防御没动）—— 是「下降」，不是「有得有失」。</summary>
    public const string ReasonOffenseDown = "Verdict_Reason_OffenseDown";
    /// <summary>只有防御这一维下降（进攻没动）。</summary>
    public const string ReasonDefenseDown = "Verdict_Reason_DefenseDown";

    /// <summary>
    /// 判定（**顺序即优先级**，照这个顺序写、不要自创；列表序号是档位标号，
    /// ⑦ 那一档在代码里是三个分支 7a / 7b / 7c，下面按**代码顺序**逐条列出）：
    /// <list type="number">
    /// <item><see cref="ItemVerdictInput.HardGateFailed"/> → Tradeoff（<see cref="ReasonHardGate"/>）</item>
    /// <item>两维都 null → Unresolved（<see cref="ReasonNoMetrics"/>）</item>
    /// <item><see cref="ItemVerdictInput.ResistanceDeficitWorsened"/> → Tradeoff（<see cref="ReasonResCap"/>）</item>
    /// <item>两维都在 ±1% 内 → NoChange（<see cref="ReasonNoChange"/>）</item>
    /// <item>off ≥ +1 且 def &gt; -1：def ≥ +1 时攻防双升（都 ≥ +5 是 StrongUpgrade，否则 ClearUpgrade，
    ///       <see cref="ReasonBothUp"/>）；否则 OffenseUpgrade（<see cref="ReasonOffense"/>）</item>
    /// <item>def ≥ +1 且 off &gt; -1 → DefenseUpgrade（<see cref="ReasonDefense"/>）</item>
    /// <item><b>7a</b>：off ≤ -1 且 def ≤ -1 → 双降（都 ≤ -5 是 StrongDowngrade，否则 Downgrade，<see cref="ReasonBothDown"/>）</item>
    /// <item><b>7b</b>：off ≤ -1 且 **def 在 ±1% 内**（防御没动）→ Downgrade（<see cref="ReasonOffenseDown"/>）——
    ///       这是「下降」，**不是**「一升一降」。⚠ 判据是「另一维在容差内」，不是「另一维 &gt; -1%」：
    ///       否则 (进攻 +3%、防御 -1%) 这种真·一升一降会被误判成「只有防御降」。</item>
    /// <item><b>7c</b>：def ≤ -1 且 **off 在 ±1% 内** → Downgrade（<see cref="ReasonDefenseDown"/>）—— 7b 的镜像</item>
    /// <item><b>8</b>：其余（两维都有数且**确实**一升一降）→ Tradeoff（<see cref="ReasonMixed"/>）</item>
    /// <item><b>9</b>：只有一维有数（DPS 不可用 / 只有 EHP）→ 按那一维定：
    ///       ≥ +1 → DefenseUpgrade，≤ -1 → Downgrade，其余 NoChange（<see cref="ReasonOnlyEhp"/>）</item>
    /// </list>
    /// </summary>
    public static (ItemVerdict Verdict, string ReasonKey) Decide(ItemVerdictInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ① 用户自己勾的硬性门槛没过：这是他自己的要求，压过数值上的一切结论。
        if (input.HardGateFailed)
        {
            return (ItemVerdict.Tradeoff, ReasonHardGate);
        }

        var offense = input.DpsPercent;
        var defense = input.EhpPercent;

        // ② 两维都拿不到数：引擎给不出可用指标。**只有这一条**允许两维都没数。
        if (offense is null && defense is null)
        {
            return (ItemVerdict.Unresolved, ReasonNoMetrics);
        }

        // ③ 已经顶到上限的抗性被拉低：这不是「持平」，是实打实的缺口变糟。
        if (input.ResistanceDeficitWorsened)
        {
            return (ItemVerdict.Tradeoff, ReasonResCap);
        }

        // ④ 两维都在 ±1% 内（**严格小于**：正好 ±1% 归下一条，见类注释第 3 条）。
        if (offense is { } flatOffense && defense is { } flatDefense
            && Math.Abs(flatOffense) < SmallPercent
            && Math.Abs(flatDefense) < SmallPercent)
        {
            return (ItemVerdict.NoChange, ReasonNoChange);
        }

        // ⑤ 进攻提升、防御没降（def > -1 允许防御小幅波动）。
        if (offense is { } upOffense && defense is { } nearDefense
            && upOffense >= SmallPercent && nearDefense > -SmallPercent)
        {
            if (nearDefense >= SmallPercent)
            {
                var strong = upOffense >= LargePercent && nearDefense >= LargePercent;
                return (strong ? ItemVerdict.StrongUpgrade : ItemVerdict.ClearUpgrade, ReasonBothUp);
            }

            return (ItemVerdict.OffenseUpgrade, ReasonOffense);
        }

        // ⑥ 防御提升、进攻没降。
        if (offense is { } nearOffense && defense is { } upDefense
            && upDefense >= SmallPercent && nearOffense > -SmallPercent)
        {
            return (ItemVerdict.DefenseUpgrade, ReasonDefense);
        }

        // ⑦a 攻防双降。
        if (offense is { } downOffense && defense is { } downDefense
            && downOffense <= -SmallPercent && downDefense <= -SmallPercent)
        {
            var strong = downOffense <= -LargePercent && downDefense <= -LargePercent;
            return (strong ? ItemVerdict.StrongDowngrade : ItemVerdict.Downgrade, ReasonBothDown);
        }

        // ⑦b 只有进攻降、防御**没动**（在 ±1% 内）：这是「下降」，不是「有得有失」——
        //     原来它会掉到 ⑧ 去、被说成「一个升一个降」（文案说反了）。
        //     ⚠ 判据必须是「另一维在容差内」，不是「另一维 > -1%」：
        //       否则 (进攻 +3%、防御 -1%) 这种**真·一升一降**会被误判成「只有防御降」。
        if (offense is { } offenseDown && defense is { } flatDefense2
            && offenseDown <= -SmallPercent && Math.Abs(flatDefense2) < SmallPercent)
        {
            return (ItemVerdict.Downgrade, ReasonOffenseDown);
        }

        // ⑦c 只有防御降、进攻没动（同上，镜像）。
        if (defense is { } defenseDown && offense is { } flatOffense2
            && defenseDown <= -SmallPercent && Math.Abs(flatOffense2) < SmallPercent)
        {
            return (ItemVerdict.Downgrade, ReasonDefenseDown);
        }

        // ⑧ 其余（两维都有数且**确实**一升一降）。
        if (offense is not null && defense is not null)
        {
            return (ItemVerdict.Tradeoff, ReasonMixed);
        }

        // ⑨ 「只有一维有数」这条在输入里被**点明**了（MetricsPartial）—— 按有数的那一维定结论。
        // ⚠ MetricsPartial **不改变结论**，只把调用方的意图写在类型上：本调用方下
        //   「offense 为 null」与「MetricsPartial 为 true」是同一个布尔（都源自 DpsUnavailable），
        //   所以它两条分支逐条判据、逐条返回都相同 —— 判据始终是「另一维为 null」，别把它当开关。
        //   （审计 R2：原来那句「本轮把它接上了线」会让人以为行为变了，实际是两份逐条相同的代码。）
        //   它把「只有一维有数」这件事在语义上点明，判据本身仍然是「另一维是 null」——
        //   所以「两维都有数」时它就算被误置位也不会改变结论（下面这条 else-if 会先被跳过）。
        if (input.MetricsPartial && defense is { } partialEhp)
        {
            if (partialEhp >= SmallPercent)
            {
                return (ItemVerdict.DefenseUpgrade, ReasonOnlyEhp);
            }

            if (partialEhp <= -SmallPercent)
            {
                return (ItemVerdict.Downgrade, ReasonOnlyEhp);
            }

            return (ItemVerdict.NoChange, ReasonOnlyEhp);
        }

        // ⑨（兜底形态）只剩一维有数，但输入没点明 MetricsPartial —— 按同一套判据走，别在这里另造结论。
        if (defense is { } onlyEhp)
        {
            // DpsUnavailable = 只有 EHP 有数（规格里写明的形态）。
            if (onlyEhp >= SmallPercent)
            {
                return (ItemVerdict.DefenseUpgrade, ReasonOnlyEhp);
            }

            if (onlyEhp <= -SmallPercent)
            {
                return (ItemVerdict.Downgrade, ReasonOnlyEhp);
            }

            return (ItemVerdict.NoChange, ReasonOnlyEhp);
        }

        // 对称的另一半：只有 DPS 有数（EHP 这一维算不出来）。规格没写，镜像处理（见 ReasonOnlyDps）。
        var onlyOffense = offense!.Value;
        if (onlyOffense >= SmallPercent)
        {
            return (ItemVerdict.OffenseUpgrade, ReasonOnlyDps);
        }

        if (onlyOffense <= -SmallPercent)
        {
            return (ItemVerdict.Downgrade, ReasonOnlyDps);
        }

        return (ItemVerdict.NoChange, ReasonOnlyDps);
    }
}
