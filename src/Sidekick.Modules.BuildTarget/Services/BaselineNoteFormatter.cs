using System.Globalization;
using Microsoft.Extensions.Localization;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 把基准统计拼成界面上的话：
/// 「10 个部位中 8 个有基准（6 个来自导入的 BD，2 个手动采集）」。
/// 中英语序不同，所以整句由资源键控制、这里只负责填数与拼接。
/// </summary>
public static class BaselineNoteFormatter
{
    /// <summary>有基准的部位数 + 各来源的拆解。</summary>
    public static string Format(BaselineSummary summary, IStringLocalizer<BuildTargetResources> resources)
    {
        if (summary.WithBaseline <= 0)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                resources["Baseline_Summary_None"].Value,
                summary.TotalSlots);
        }

        var parts = new List<string>();
        AddPart(parts, summary.FromBuild, "Baseline_From_Bd", resources);
        AddPart(parts, summary.Manual, "Baseline_Manual", resources);
        AddPart(parts, summary.Unknown, "Baseline_Unknown", resources);

        return string.Format(
            CultureInfo.CurrentCulture,
            resources["Baseline_Summary"].Value,
            summary.TotalSlots,
            summary.WithBaseline,
            string.Join(resources["Baseline_List_Separator"].Value, parts));
    }

    /// <summary>单个部位的来源标签；老数据没有来源标记时是「来源未知」。</summary>
    public static string SourceLabel(string? source, IStringLocalizer<BuildTargetResources> resources) => source switch
    {
        BaselineSources.Build => resources["Baseline_Source_Bd"].Value,
        BaselineSources.Manual => resources["Baseline_Source_Manual"].Value,
        _ => resources["Baseline_Source_Unknown"].Value,
    };

    private static void AddPart(List<string> parts, int count, string key, IStringLocalizer<BuildTargetResources> resources)
    {
        if (count > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, resources[key].Value, count));
        }
    }
}
