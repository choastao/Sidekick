namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 基准来源的取值（存在 <see cref="BuildTargetTemplate.EquippedSource"/> 里）。
/// </summary>
public static class BaselineSources
{
    /// <summary>导入 BD 时写进去的基准（BD 里当时穿的装备）。</summary>
    public const string Build = "bd";

    /// <summary>游戏内悬停采集的基准，比 BD 里的准，覆盖 BD 的值。</summary>
    public const string Manual = "manual";

    /// <summary>老数据：有基准值但没有来源标记，界面按「来源未知」显示，不猜。</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// 把一个部位记为手动采集：原文留下，来源标记从 bd 改成 manual（值以游戏内采到的为准）。
    /// 手动采的更准——那是用户真实穿的装备，所以来源会被覆盖成 manual。
    /// </summary>
    public static void MarkManual(BuildTargetTemplate template, string slotKey, string itemText)
    {
        template.Equipped[slotKey] = itemText;
        template.EquippedSource[slotKey] = Manual;
    }
}

/// <summary>当前装备基准的覆盖情况与来源统计。</summary>
public class BaselineSummary
{
    /// <summary>部位总数（见 <see cref="SlotKeys.All"/>）。</summary>
    public int TotalSlots { get; set; } = SlotKeys.All.Length;

    /// <summary>有基准的部位数。</summary>
    public int WithBaseline { get; set; }

    /// <summary>其中来自导入 BD 的部位数。</summary>
    public int FromBuild { get; set; }

    /// <summary>其中游戏内手动采集的部位数。</summary>
    public int Manual { get; set; }

    /// <summary>其中有基准值、但没来源标记的老数据。</summary>
    public int Unknown { get; set; }
}
