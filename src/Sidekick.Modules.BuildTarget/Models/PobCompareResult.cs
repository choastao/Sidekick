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

    public double DpsDelta => HasDelta ? Current!.Dps - Base!.Dps : 0;

    public double EhpDelta => HasDelta ? Current!.Ehp - Base!.Ehp : 0;

    /// <summary>相对变化（%）。基线为 0 时返回 null —— 不编一个除不出来的百分比。</summary>
    public double? DpsPercent => HasDelta && Base!.Dps > 0 ? DpsDelta / Base!.Dps * 100 : null;

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
        };
    }

    public static PobCompareResult Not(PobCompareStatus status, string? error = null) => new()
    {
        Status = status,
        Error = error,
    };
}
