namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>引擎给出的一组数值。绝对值只能当参考（PoB 是模拟器，配置错就偏），**相对差值才是我们要的**。</summary>
public sealed record PobStats(double Dps, double Ehp, double Life)
{
    /// <summary>
    /// PoB 的 <c>CombinedDPS</c>（主技能 + 其它技能的合计）。
    /// **不是**默认主指标，只在 <see cref="Dps"/> 为 0 时按回退链使用（见 <see cref="PobPrimaryMetric"/>）。
    /// </summary>
    public double CombinedDps { get; init; }

    /// <summary>
    /// PoB 的 <c>FullDPS</c>（全部技能合计）。同 <see cref="CombinedDps"/>，只在回退链里用。
    /// </summary>
    public double FullDps { get; init; }

    /// <summary>
    /// **有效**火抗（引擎 <c>mainOutput</c> 的口径）。<c>null</c> = helper 没报这个字段（老 helper）。
    ///
    /// ⚠ 可空是刻意的：**缺失 ≠ 0**。0 会让「抗性上限守门」（<see cref="ResistanceGuardrail"/>）
    /// 把「不知道」当成「这项抗性本来就是 0」——那是两种完全不同的结论。
    /// </summary>
    public double? FireResist { get; init; }

    /// <summary>有效冰抗；<c>null</c> = 不知道（见 <see cref="FireResist"/>）。</summary>
    public double? ColdResist { get; init; }

    /// <summary>有效电抗；<c>null</c> = 不知道（见 <see cref="FireResist"/>）。</summary>
    public double? LightningResist { get; init; }

    /// <summary>有效混沌抗；<c>null</c> = 不知道（见 <see cref="FireResist"/>）。守门不用它（混沌抗上限另算）。</summary>
    public double? ChaosResist { get; init; }

    /// <summary>
    /// 火抗**溢出**上限的数值（&gt; 0 = 已经顶到上限之上）；<c>null</c> = 不知道。
    /// 口径：helper 用引擎的 <c>FireResistTotal - FireResist</c> 算出来（引擎里各元素的溢出字段名不统一，
    /// 火/电有 Over、冰没有、混沌叫 OverCap，所以统一走 Total - Resist）。
    /// </summary>
    public double? FireResistOver { get; init; }

    /// <summary>冰抗溢出；<c>null</c> = 不知道（见 <see cref="FireResistOver"/>）。</summary>
    public double? ColdResistOver { get; init; }

    /// <summary>电抗溢出；<c>null</c> = 不知道（见 <see cref="FireResistOver"/>）。</summary>
    public double? LightningResistOver { get; init; }

    /// <summary>
    /// 这份数值是**按哪个评估场景**算的（<c>BUILD</c> / <c>MAP</c> / <c>BOSS</c>）；<c>null</c> = 按 BD 原样。
    ///
    /// 为什么数值对象要带场景：helper 没回 <c>context</c> 时（老 helper）请求的 MAP / BOSS 覆盖
    /// **压根没生效**，数值是按 BD 自己的配置算的。界面照旧写「评估场景：打王」就是假话 ——
    /// 语义与 <see cref="FireResist"/> 那条一样：**缺失不等于默认值**。
    /// </summary>
    public string? RequestedContext { get; init; }

    /// <summary>
    /// 引擎**确认**了 <see cref="RequestedContext"/> 已经生效（helper 回了 <c>context</c>）。
    ///
    /// <c>false</c> + <see cref="RequestedContext"/> 非 null = **请求了那个场景，但引擎没确认** ——
    /// 数值很可能是 BD 原样。界面必须如实说「场景未生效 / 未确认」，而不是照抄设置里的标签。
    ///
    /// ⚠ 默认 <c>true</c>（可按 BD 原样算的 <c>BUILD</c> 请求 + 单测里手搓的 stats），
    ///   只有真的「请求了非 BUILD 场景却没拿到确认」才会被置成 false。
    /// </summary>
    public bool ContextConfirmed { get; init; } = true;

    /// <summary>
    /// 抗性守门（<see cref="ResistanceGuardrail"/>）要看的**六项（火 / 冰 / 电 的 <c>Resist</c> 与 <c>ResistOver</c>）全都有数**。
    ///
    /// <c>false</c> = 老 helper 没报全 → 界面要出提示（不阻塞计算），说明**守门对缺的那些项不生效**。
    /// 缺哪几项由 <see cref="PobStats"/> 里那几个可空属性自己说；这里只回「全不全」。
    ///
    /// ⚠ **判据必须跟消费者对齐**：<see cref="ResistanceGuardrail"/> 只读六项 ——
    /// 火 / 冰 / 电 的 <c>Resist</c> 与 <c>ResistOver</c>；**混沌抗它压根不碰**（上限另算，见 <see cref="PobStats.ChaosResist"/> 的注释）。
    /// 所以这里**不把 <c>ChaosResist</c> 算进判据**：否则一个只缺混沌抗（引擎侧字段名最不统一的那一项）的 helper
    /// 会让界面说「守门没有生效」，而事实是火/冰/电三项都在守 —— 把「逐元素」说成了全局开关。
    /// </summary>
    public bool ResistanceDataComplete =>
        FireResist != null && ColdResist != null && LightningResist != null
        && FireResistOver != null && ColdResistOver != null && LightningResistOver != null;

    /// <summary>
    /// 请求了非 <c>BUILD</c> 的场景（刷图 / 打王）但引擎**没确认**它生效 ——
    /// 这份数值很可能是按 BD 原样算的。界面必须说「场景未生效」，不许照抄设置里的标签。
    /// （值语义上等价于「<see cref="RequestedContext"/> 非 null 且 <see cref="ContextConfirmed"/> 为 false」。）
    /// </summary>
    public bool ContextUnconfirmed => RequestedContext != null && !ContextConfirmed;

    /// <summary>抗性守门要看的那**六项**没报全 → 守门**对缺的那些项**不生效（不是全局失效），界面要出提示（见 <see cref="ResistanceDataComplete"/>）。</summary>
    public bool ResistanceDataIncomplete => !ResistanceDataComplete;
}

public enum PobCompareStatus
{
    /// <summary>算出来了。</summary>
    Success,

    /// <summary>设置里的 PoB 引擎开关没打开。</summary>
    Disabled,

    /// <summary>还没有「目标BD」模板。</summary>
    NoTemplate,

    /// <summary>模板里没有 BD 源码（老模板）—— 需要重新导入一次 BD。</summary>
    NoBuildXml,

    /// <summary>这个部位不在 PoB 的槽位体系里。</summary>
    UnsupportedSlot,

    /// <summary>珠宝：镶在天赋树上（PoB 是 <c>SocketIdURL nodeId</c>），不是穿戴槽位，暂不支持试穿。</summary>
    SocketedItem,

    /// <summary>引擎没起来（没装引擎、或进程崩了/超时）。</summary>
    EngineUnavailable,

    /// <summary>物品基底取不到英文名，PoB 认不出这件物品（不能拿它去试穿）。</summary>
    BaseUnknown,

    /// <summary>中文 → 英文转换失败。</summary>
    ConversionFailed,

    /// <summary>
    /// 备选篮里的旧条目没有存物品原文（v3.7 之前只存算好的贡献值）→ 拿不去试穿。
    /// 让它重新入篮一次即可，**不要**去猜原文。
    /// </summary>
    MissingText,

    /// <summary>
    /// **转换层自己的缺陷**：生成的文本里定位不到这条词缀（C2b/C2c 造变体时）。
    /// 与「引擎出错」是两回事 —— 引擎压根没被调用过，别把它甩到引擎头上。
    /// </summary>
    AffixLost,

    /// <summary>其它失败（引擎原样带回的报错）。</summary>
    Failed,
}

/// <summary>
/// 一次「试穿对比」的结果。**界面只认 <see cref="HasDelta"/>**：
/// 没算出来时把 <see cref="Status"/> 翻译成一句人话显示，绝不给出数字。
/// </summary>
public sealed class PobCompareResult
{
    public PobCompareStatus Status { get; init; }

    public PobStats? Base { get; init; }

    public PobStats? Current { get; init; }

    /// <summary>
    /// 这次对比用的是哪个伤害指标（<c>TotalDPS</c> / <c>CombinedDPS</c> / <c>FullDPS</c>，见
    /// <see cref="PobPrimaryMetric"/>）。空串 = 一个数字都没有（<see cref="DpsUnavailable"/> 为真）。
    ///
    /// ⚠ 一定要说清楚用的是哪个数：回退到 Combined/Full 时，绝对值与默认的 TotalDPS **不是一回事**，
    /// 不说就会让用户拿两个不同口径的数对照。
    /// </summary>
    public string PrimaryMetricKey { get; init; } = "";

    /// <summary>
    /// 引擎算不出这份 BD 的伤害（三个字段全为 0 / 负，召唤 / 触发类 BD 的典型症状）。
    ///
    /// **界面必须把 DPS 那一列显示成「—」而不是 0**：0 会被读成「没变化」，
    /// 而事实是「这个数压根算不出来」——两种结论完全相反。
    /// </summary>
    public bool DpsUnavailable { get; init; }

    /// <summary>
    /// 基线那一侧**所选主指标**的数值（= <see cref="PobPrimaryMetric.Value"/>(<see cref="Base"/>, <see cref="PrimaryMetricKey"/>)）。
    ///
    /// ⚠ **不是** <c>Base.Dps</c>：回退到 <c>CombinedDPS</c> / <c>FullDPS</c> 时 <c>Base.Dps</c> 是 0，
    /// 拿它当「原来多少」就等于把「引擎给不出 TotalDPS」说成「原来一点伤害都没有」。
    /// </summary>
    public double BaseMetricValue { get; init; }

    /// <summary>候选那一侧**同一个主指标**的数值（同 <see cref="BaseMetricValue"/>，两侧必须同 key）。</summary>
    public double CurrentMetricValue { get; init; }

    /// <summary>
    /// **所选主指标**的增减（<see cref="CurrentMetricValue"/> − <see cref="BaseMetricValue"/>），
    /// 即备选篮排序真正要用的那个数（= 基准那一份 <see cref="DpsDelta"/>，但语义说出来而不靠巧合）。
    ///
    /// 排序拿**差值**而不是绝对值：绝对 DPS 高的候选不一定是一件更好的装备
    /// （见 <c>PobAffixGainService</c> 的「拿掉这条词缀能差多少」口径）。
    /// </summary>
    public double MetricDelta => CurrentMetricValue - BaseMetricValue;

    /// <summary>
    /// **我们的转换层**没认出、因而压根没发出去的词缀条数（> 0 时这几条没参与计算）。
    /// ⚠ 与「引擎不支持」是两件事（见 <see cref="EngineUnsupportedLines"/>）：这一条是**我们**的缺口
    /// （词缀压根没发出去），说成「引擎不支持」就是甩锅给引擎 —— 旧文案犯过这个错，界面不许再出现。
    /// </summary>
    public int UnmappedAffixes { get; init; }

    /// <summary>
    /// **引擎自己不支持**的词缀行原文（PoB 自己标成 "Not supported in PoB yet"，
    /// 这类行会被它跳过、不参与计算；helper 从 <c>modLine.extra</c> 读出来透传）。
    ///
    /// 为什么要逐条说出来：这类词缀进算式时是 0，而界面上的「0」会被读成「这条词缀没贡献」——
    /// 于是用户会得出「火抗在这件装备上不值钱」这种错误结论。引擎的盲区必须标成盲区。
    /// </summary>
    public IReadOnlyList<string> EngineUnsupportedLines { get; init; } = [];

    public int EngineUnsupportedCount => EngineUnsupportedLines.Count;

    public string? Error { get; init; }

    public bool HasDelta => Status == PobCompareStatus.Success && Base != null && Current != null;

    /// <summary>
    /// **所选主指标**的差值（<see cref="CurrentMetricValue"/> − <see cref="BaseMetricValue"/>）。
    ///
    /// ⚠ 名字是历史遗留（界面与既有测试都在用，不改），但**它不再恒等于 <c>TotalDPS</c> 的差**：
    /// 引擎给不出 <c>TotalDPS</c> 时，用 <see cref="PrimaryMetricKey"/> 那个指标的两侧数值相减
    /// （见 <see cref="PobPrimaryMetric"/>）。属性名不许改，但读它的人必须知道这一点。
    /// </summary>
    public double DpsDelta => HasDelta ? CurrentMetricValue - BaseMetricValue : 0;

    public double EhpDelta => HasDelta ? Current!.Ehp - Base!.Ehp : 0;

    /// <summary>
    /// 相对变化（%），基数是**所选主指标**的基线值（<see cref="BaseMetricValue"/>，**不是** <c>Base.Dps</c>）。
    /// 基线为 0（= 这个指标也算不出来）时返回 null —— 不编一个除不出来的百分比。
    ///
    /// ⚠ 名字同样是历史遗留：它说的是「<see cref="PrimaryMetricKey"/> 那个指标涨了几个百分点」，
    /// 默认档（<c>TotalDPS</c>）下与改动前逐位一致。
    /// </summary>
    public double? DpsPercent => HasDelta && BaseMetricValue > 0 ? DpsDelta / BaseMetricValue * 100 : null;

    public double? EhpPercent => HasDelta && Base!.Ehp > 0 ? EhpDelta / Base!.Ehp * 100 : null;

    public static PobCompareResult Ok(
        PobStats baseline,
        PobStats current,
        int unmappedAffixes,
        IReadOnlyList<string>? engineUnsupported = null)
    {
        // ⚠ 主指标**只按基线那份 stats 选**（不能一处用基线、一处用候选去选）：
        //   两处若选了不同的回退档（例如基线回退到 Combined、候选没回退），
        //   「涨了多少」就变成两个不同口径的数相减 —— 那是静默错数。
        var metric = PobPrimaryMetric.Select(baseline);

        return new()
        {
            Status = PobCompareStatus.Success,
            Base = baseline,
            Current = current,
            UnmappedAffixes = unmappedAffixes,
            EngineUnsupportedLines = engineUnsupported ?? [],
            PrimaryMetricKey = metric.Key,
            DpsUnavailable = !metric.Resolved,
            // ⚠ 两边都用**同一个 key**取数（基线选出来的那个），同口径相减 ——
            //   一侧用 TotalDPS、一侧用 CombinedDPS 会让「涨了多少」变成两个口径的数相减。
            BaseMetricValue = PobPrimaryMetric.Value(baseline, metric.Key),
            CurrentMetricValue = PobPrimaryMetric.Value(current, metric.Key),
        };
    }

    public static PobCompareResult Not(PobCompareStatus status, string? error = null) => new()
    {
        Status = status,
        Error = error,
    };
}
