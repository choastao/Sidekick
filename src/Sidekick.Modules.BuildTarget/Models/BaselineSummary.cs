namespace Sidekick.Modules.BuildTarget.Models;

using Sidekick.Game.Parser.Items;

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
    ///
    /// ⚠ 必须同时换掉 <c>EquippedStats</c> / <c>EquippedNames</c>：那是导入 BD 时抽的
    /// 「BD 当时穿的那件」的数值。快照换人后如果不换它，一旦这份快照解析失败，
    /// 评估会退回用 BD 的数值冒充「你的当前装备」，静默算出错误结论。
    /// 解析不出来时宁可清空（退回「没有基准」），也不要留着别人的数。
    /// </summary>
    public static void MarkManual(BuildTargetTemplate template, string slotKey, string itemText, Item? parsed)
    {
        template.Equipped[slotKey] = itemText;
        template.EquippedSource[slotKey] = Manual;

        if (parsed == null)
        {
            template.EquippedStats.Remove(slotKey);
            template.EquippedNames.Remove(slotKey);
            return;
        }

        var stats = new Dictionary<string, double>();
        foreach (var stat in parsed.Stats)
        {
            if (string.IsNullOrWhiteSpace(stat.Text))
            {
                continue;
            }

            stats[stat.Text] = stat.Values.Count > 0 ? stat.AverageValue : stat.Values.Sum();
        }

        template.EquippedStats[slotKey] = stats;

        var name = parsed.Name ?? parsed.Type;
        if (!string.IsNullOrWhiteSpace(name))
        {
            template.EquippedNames[slotKey] = name;
        }
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
