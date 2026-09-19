using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Sidekick.Game.Parser;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 把一件新装备对照目标 BD 模板做评估：部位门槛 + 与当前装备快照的增减 + 角色级合计估算。
///
/// 只做「加法类词缀」的比较（生命 / 能量护盾 / 抗性等）——这些在 PoE 里就是直接相加，
/// 不接 PoB 引擎也能算准。DPS / EHP 这类依赖整套 BD 结构的数值不在这里算，避免给出错误结论。
/// </summary>
public class BuildTargetEvaluator
{
    private static readonly Regex NumberRegex = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private readonly ItemParser itemParser;
    private readonly IStringLocalizer<BuildTargetResources> resources;

    public BuildTargetEvaluator(ItemParser itemParser, IStringLocalizer<BuildTargetResources> resources)
    {
        this.itemParser = itemParser;
        this.resources = resources;
    }

    public BuildTargetResult Evaluate(BuildTargetTemplate? template, Item newItem)
    {
        var result = new BuildTargetResult();

        if (template == null)
        {
            result.Headline = resources["Result_No_Template"];
            return result;
        }

        result.HasTemplate = true;
        result.NewItemName = newItem.Name ?? newItem.Type;

        // 基准统计：哪些部位有基准、分别来自导入的 BD 还是手动采集
        result.Baseline = BaselineSummaryCalculator.Compute(template, slotKey => ParseEquipped(template, slotKey) != null);

        var slotKeys = SlotKeys.ResolveFor(newItem);
        if (slotKeys.Length == 0)
        {
            result.Headline = resources["Result_Unknown_Slot"];
            return result;
        }

        foreach (var slotKey in slotKeys)
        {
            var evaluation = EvaluateSlot(template, newItem, slotKey);
            if (evaluation.Checks.Count > 0 || evaluation.HasEquipped)
            {
                result.Slots.Add(evaluation);
            }
        }

        result.Character = EvaluateCharacter(template, newItem, slotKeys, result.Baseline);

        var primary = result.Slots.FirstOrDefault();
        result.Verdict = primary?.Verdict ?? Verdict.Unknown;
        result.Headline = primary?.Headline ?? resources["Result_No_Slot_Target"];

        return result;
    }

    private SlotEvaluation EvaluateSlot(BuildTargetTemplate template, Item newItem, string slotKey)
    {
        var targets = template.Slots.TryGetValue(slotKey, out var list) ? list : [];
        var hasSnapshot = template.Equipped.TryGetValue(slotKey, out var snapshotText) && !string.IsNullOrWhiteSpace(snapshotText);
        var currentItem = hasSnapshot ? ParseEquipped(template, slotKey) : null;
        var imported = GetImportedStats(template, slotKey);

        var evaluation = new SlotEvaluation
        {
            SlotKey = slotKey,
            SlotLabel = SlotLabel(slotKey),
            HasSnapshot = hasSnapshot || imported != null,
            HasEquipped = currentItem != null || imported != null,
            EquippedName = currentItem?.Name ?? currentItem?.Type ?? GetImportedName(template, slotKey),
            BaselineSource = currentItem != null || imported != null ? BaselineSummaryCalculator.Source(template, slotKey) : null,
            // 用户把自己身上那件复制了一遍：文本一模一样就认定是同一件，不再给换装建议。
            IsCurrentItem = hasSnapshot && TextKey(newItem.Text.Text) == TextKey(snapshotText!),
        };

        foreach (var target in targets)
        {
            var (newValue, newLine) = FindStat(newItem, target);
            double? currentValue = null;
            if (currentItem != null)
            {
                (currentValue, _) = FindStat(currentItem, target);
            }
            else if (imported != null)
            {
                // 解析不了快照（多是导入 BD 的英文物品文本）时，用导入时抽好的数值
                currentValue = LookupImported(imported, target);
            }

            evaluation.Checks.Add(new ModCheck
            {
                Label = string.IsNullOrWhiteSpace(target.Label) ? string.Join(" / ", target.Match) : target.Label,
                MinValue = target.MinValue,
                Required = target.Required,
                New = newValue,
                Current = currentValue,
                MatchedLine = newLine,
                Match = [.. target.Match],
            });
        }

        ComputeVerdict(evaluation);
        return evaluation;
    }

    /// <summary>
    /// 判定结论。逻辑在 <see cref="VerdictDecider"/>（纯函数，可单独测试）。
    /// </summary>
    private void ComputeVerdict(SlotEvaluation evaluation)
    {
        var (verdict, headline) = VerdictDecider.Decide(evaluation, resources);
        evaluation.Verdict = verdict;
        evaluation.Headline = headline;
    }

    private List<CharacterEstimate> EvaluateCharacter(BuildTargetTemplate template, Item newItem, string[] newItemSlotKeys, BaselineSummary baseline)
    {
        var results = new List<CharacterEstimate>();
        if (template.Character.Count == 0)
        {
            return results;
        }

        var snapshots = new Dictionary<string, Item>();
        foreach (var slotKey in SlotKeys.All)
        {
            var parsed = ParseEquipped(template, slotKey);
            if (parsed != null)
            {
                snapshots[slotKey] = parsed;
            }
        }

        // 导入 BD 抽好的数值（实际快照解析不出来时才用）
        var importedBySlot = new Dictionary<string, Dictionary<string, double>>();
        foreach (var slotKey in SlotKeys.All)
        {
            if (snapshots.ContainsKey(slotKey))
            {
                continue;
            }

            var stats = GetImportedStats(template, slotKey);
            if (stats != null)
            {
                importedBySlot[slotKey] = stats;
            }
        }

        // 和界面上的「X 个部位有基准」用同一个数，避免两处对不上
        var collectedSlots = baseline.WithBaseline;

        foreach (var target in template.Character)
        {
            var estimate = new CharacterEstimate
            {
                Label = string.IsNullOrWhiteSpace(target.Label) ? string.Join(" / ", target.Match) : target.Label,
                MinValue = target.MinValue,
                CollectedSlots = collectedSlots,
            };

            if (collectedSlots > 0)
            {
                var sum = snapshots.Values.Sum(x => FindStat(x, target).Value ?? 0);
                sum += importedBySlot.Values.Sum(x => LookupImported(x, target) ?? 0);
                estimate.CurrentSum = sum;
            }

            // 角色级合计：把新装备替换进去。戒指有两槽时，取第一个已采集的槽作为被替换基准。
            double replaced = 0;
            var replacedSlot = newItemSlotKeys.FirstOrDefault(x => snapshots.ContainsKey(x));
            if (replacedSlot != null)
            {
                replaced = FindStat(snapshots[replacedSlot], target).Value ?? 0;
            }
            else
            {
                var importedSlot = newItemSlotKeys.FirstOrDefault(x => importedBySlot.ContainsKey(x));
                if (importedSlot != null)
                {
                    replaced = LookupImported(importedBySlot[importedSlot], target) ?? 0;
                }
            }

            var (newValue, _) = FindStat(newItem, target);
            if (estimate.CurrentSum.HasValue)
            {
                estimate.NewSum = estimate.CurrentSum.Value - replaced + (newValue ?? 0);
            }
            else if (newValue.HasValue)
            {
                estimate.NewSum = newValue.Value;
            }

            results.Add(estimate);
        }

        return results;
    }

    private Item? ParseEquipped(BuildTargetTemplate template, string slotKey)
    {
        if (!template.Equipped.TryGetValue(slotKey, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return itemParser.ParseItem(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在物品上找目标词缀。优先用解析后的词缀行（数值已拆好），找不到再退回扫原始文本行。
    /// </summary>
    private static (double? Value, string? Line) FindStat(Item item, ModTarget target)
    {
        if (target.Match.Count == 0)
        {
            return (null, null);
        }

        foreach (var stat in item.Stats)
        {
            if (Matches(stat.Text, target))
            {
                return (stat.Values.Count > 0 ? stat.AverageValue : stat.Values.Sum(), stat.Text);
            }
        }

        foreach (var line in item.Text.Text.Split('\n'))
        {
            if (Matches(line, target))
            {
                var match = NumberRegex.Match(line);
                if (match.Success && double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    return (value, line.Trim());
                }

                return (null, line.Trim());
            }
        }

        return (null, null);
    }

    private static bool Matches(string? text, ModTarget target)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var pattern in target.Match)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            // 手填关键词：直接子串包含（中英不敏感）
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 从词缀选择器插入的模板：`#` 代表数值，按通配匹配
            // 例如「增加#%冷卻時間恢復率」要能匹配「增加20%冷卻時間恢復率」
            if (pattern.Contains('#'))
            {
                var regex = Regex.Escape(pattern).Replace("\\#", NumberWildcard).Replace("#", NumberWildcard);
                try
                {
                    if (Regex.IsMatch(text, regex, RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // 用户手填的非法正则，忽略
                }
            }
        }

        return false;
    }

    /// <summary>匹配物品行里的数值（含小数/正负号/千分位）。</summary>
    private const string NumberWildcard = @"[\d\.,\-+]*";

    /// <summary>取导入 BD 时抽好的某个部位的（词缀名 -&gt; 数值）表，没有则返回 null。</summary>
    private static Dictionary<string, double>? GetImportedStats(BuildTargetTemplate template, string slotKey)
        => template.EquippedStats.TryGetValue(slotKey, out var map) && map.Count > 0 ? map : null;

    private static string? GetImportedName(BuildTargetTemplate template, string slotKey)
        => template.EquippedNames.TryGetValue(slotKey, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;

    /// <summary>在导入 BD 抽出的（词缀名 -&gt; 数值）表里按关键词找目标词缀。</summary>
    private static double? LookupImported(Dictionary<string, double> map, ModTarget target)
    {
        if (target.Match.Count == 0)
        {
            return null;
        }

        foreach (var row in map)
        {
            if (Matches(row.Key, target))
            {
                return row.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// 物品文本比对用的归一化键：统一换行、去掉空行和每行首尾空白。
    /// 用来判断「复制进来的这件」是不是就是某部位已采集的当前装备。
    /// </summary>
    private static string TextKey(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));

    private string SlotLabel(string slotKey) => resources["Slot_" + slotKey];
}
