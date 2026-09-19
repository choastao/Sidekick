using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 统计当前装备基准：哪些部位有基准、每个部位的基准来自哪里。
///
/// 「有基准」= 导入 BD 时抽好的数值表里有这个部位，或者该部位存的物品文本能解析出来。
/// 值和来源分开看：值可能在 <see cref="BuildTargetTemplate.EquippedStats"/>（导入 BD 抽的）
/// 或 <see cref="BuildTargetTemplate.Equipped"/>（游戏内采的原文，现解析现用），
/// 来源记录在 <see cref="BuildTargetTemplate.EquippedSource"/>。
/// </summary>
public static class BaselineSummaryCalculator
{
    /// <param name="template">要统计的模板，null 时返回全 0。</param>
    /// <param name="hasParsedSnapshot">
    /// 该部位的物品文本能不能解析出装备（由调用方用物品解析器判断，这里不引解析器）。
    /// 只有该部位没有导入数值时才会被问。
    /// </param>
    public static BaselineSummary Compute(BuildTargetTemplate? template, Func<string, bool> hasParsedSnapshot)
    {
        var summary = new BaselineSummary();
        if (template == null)
        {
            return summary;
        }

        foreach (var slotKey in SlotKeys.All)
        {
            if (!HasImportedStats(template, slotKey) && !hasParsedSnapshot(slotKey))
            {
                continue;
            }

            summary.WithBaseline++;
            switch (Source(template, slotKey))
            {
                case BaselineSources.Build:
                    summary.FromBuild++;
                    break;
                case BaselineSources.Manual:
                    summary.Manual++;
                    break;
                default:
                    // 老配置：有基准值但没有 EquippedSource，不猜来源。
                    summary.Unknown++;
                    break;
            }
        }

        return summary;
    }

    /// <summary>取某部位基准的来源标记；老数据没有标记时返回 null。</summary>
    public static string? Source(BuildTargetTemplate? template, string slotKey) =>
        template?.EquippedSource.TryGetValue(slotKey, out var value) == true ? value : null;

    private static bool HasImportedStats(BuildTargetTemplate template, string slotKey)
    {
        var stats = template.EquippedStats;
        return stats != null && stats.TryGetValue(slotKey, out var map) && map.Count > 0;
    }
}
