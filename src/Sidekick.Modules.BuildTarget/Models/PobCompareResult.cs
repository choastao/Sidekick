namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>引擎给出的一组数值。绝对值只能当参考（PoB 是模拟器，配置错就偏），**相对差值才是我们要的**。</summary>
public sealed record PobStats(double Dps, double Ehp, double Life);

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

    /// <summary>引擎没起来（没装引擎、或进程崩了/超时）。</summary>
    EngineUnavailable,

    /// <summary>物品基底取不到英文名，PoB 认不出这件物品（不能拿它去试穿）。</summary>
    BaseUnknown,

    /// <summary>中文 → 英文转换失败。</summary>
    ConversionFailed,

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

    /// <summary>引擎不认识的词缀条数（> 0 时结论偏乐观，界面必须如实标出来）。</summary>
    public int UnmappedAffixes { get; init; }

    public string? Error { get; init; }

    public bool HasDelta => Status == PobCompareStatus.Success && Base != null && Current != null;

    public double DpsDelta => HasDelta ? Current!.Dps - Base!.Dps : 0;

    public double EhpDelta => HasDelta ? Current!.Ehp - Base!.Ehp : 0;

    /// <summary>相对变化（%）。基线为 0 时返回 null —— 不编一个除不出来的百分比。</summary>
    public double? DpsPercent => HasDelta && Base!.Dps > 0 ? DpsDelta / Base!.Dps * 100 : null;

    public double? EhpPercent => HasDelta && Base!.Ehp > 0 ? EhpDelta / Base!.Ehp * 100 : null;

    public static PobCompareResult Ok(PobStats baseline, PobStats current, int unmappedAffixes) => new()
    {
        Status = PobCompareStatus.Success,
        Base = baseline,
        Current = current,
        UnmappedAffixes = unmappedAffixes,
    };

    public static PobCompareResult Not(PobCompareStatus status, string? error = null) => new()
    {
        Status = status,
        Error = error,
    };
}
