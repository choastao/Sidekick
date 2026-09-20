using System.Globalization;
using System.Text;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Parser.Stats;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 把 Sidekick 解析出来的物品转成 **PoB2 认得的英文 raw 文本**（纯函数，便于单测）。
///
/// 为什么需要这一步：剪贴板里的物品是**繁体中文**，而 PoB 引擎的词缀解析器只认英文。
/// 两条转换路径都走 Sidekick 已经准备好的英文数据（它为了「查价走英文站」本来就加载了）：
///   · 名称/基底 → <see cref="Item.InvariantTradeItem"/> / <see cref="Item.InvariantDefinition"/>
///   · 词缀      → stat 的 <c>TradeIds</c> 去英文 trade-stats 表里查模板（<c>#</c> 是数值占位符），
///                 再用解析出来的数值按顺序填回去。
///
/// ⚠ **不认识的词缀绝不静默丢弃**：<see cref="BuildResult.Skipped"/> 会如实报出条数，
/// 调用方要么提示用户「有 N 条词缀引擎不认识、结论偏乐观」，要么干脆不显示结论。
/// 同样地，基底名取不到英文时不猜 —— <see cref="BuildResult.BaseIdentified"/> 为 false，
/// 调用方按「这件物品算不了」处理（否则 PoB 会把它当成空物品，试穿结果 = 没有变化，属于假结论）。
/// </summary>
public static class PobItemText
{
    /// <summary>伪属性与未识别的文本行不是装备上的真词缀，给 PoB 只会添乱。</summary>
    private static readonly StatCategory[] NonAffixCategories = [StatCategory.Pseudo, StatCategory.Undefined];

    public sealed record BuildResult(string Text, int TotalStats, int MappedStats, bool BaseIdentified)
    {
        public int Skipped => TotalStats - MappedStats;
    }

    public static BuildResult Build(Item item, IReadOnlyDictionary<string, string> invariantStatText)
    {
        var lines = new List<string> { "Rarity: " + RarityText(item.Properties.Rarity) };

        // ---- 名称 + 基底（PoB 靠这两行认物品）----
        var type = FirstNonEmpty(
            item.InvariantTradeItem?.Type,
            item.InvariantDefinition?.Name);
        var baseIdentified = !string.IsNullOrWhiteSpace(type);
        if (!baseIdentified)
        {
            // 兜底用当前语言的基底名：PoB 认不出，但至少让文本看起来完整（调用方不会再拿它去试穿）
            type = item.Type;
        }

        var name = FirstNonEmpty(item.InvariantTradeItem?.Name, item.InvariantDefinition?.Name);
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, type, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(name!);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            lines.Add(type!);
        }

        // ---- 防御/等级等属性行（PoB 用 "Keyword: value" 识别）----
        AddIfPositive(lines, "Energy Shield", item.Properties.EnergyShield);
        AddIfPositive(lines, "Armour", item.Properties.Armour);
        AddIfPositive(lines, "Evasion Rating", item.Properties.EvasionRating);
        AddIfPositive(lines, "Ward", item.Properties.RunicWard);
        AddIfPositive(lines, "Quality", item.Properties.Quality);
        AddIfPositive(lines, "Item Level", item.Properties.ItemLevel);

        // ---- 词缀：隐式在前（PoB 用 "Implicits: N" 声明条数），显式在后 ----
        var totalStats = 0;
        var mapped = 0;
        var implicitLines = new List<string>();
        var explicitLines = new List<string>();

        foreach (var stat in item.Stats)
        {
            if (NonAffixCategories.Contains(stat.Category))
            {
                continue;
            }

            totalStats++;

            var template = FindTemplate(stat, invariantStatText);
            if (template == null)
            {
                continue;
            }

            var line = FillValues(template, stat.Values);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            mapped++;
            if (stat.Category == StatCategory.Implicit)
            {
                implicitLines.Add(line);
            }
            else
            {
                explicitLines.Add(line);
            }
        }

        if (implicitLines.Count > 0)
        {
            lines.Add("Implicits: " + implicitLines.Count.ToString(CultureInfo.InvariantCulture));
        }

        lines.AddRange(implicitLines);
        lines.AddRange(explicitLines);

        return new BuildResult(string.Join('\n', lines) + "\n", totalStats, mapped, baseIdentified);
    }

    /// <summary>
    /// 一条 stat 可能匹配到多个定义，取第一个能在英文表里查到的模板。
    /// 查不到就是「引擎不认识这条词缀」—— 交给调用方如实报出来。
    /// </summary>
    private static string? FindTemplate(Stat stat, IReadOnlyDictionary<string, string> invariantStatText)
    {
        foreach (var definition in stat.Definitions)
        {
            if (definition.TradeIds == null)
            {
                continue;
            }

            foreach (var id in definition.TradeIds)
            {
                if (invariantStatText.TryGetValue(id, out var template) && !string.IsNullOrWhiteSpace(template))
                {
                    return template;
                }
            }
        }

        return null;
    }

    /// <summary>把模板里的 <c>#</c> 按顺序替换成数值。数值不够时保留 #（PoB 会当它是通配，不编数字）。</summary>
    private static string FillValues(string template, IReadOnlyList<double> values)
    {
        if (!template.Contains('#'))
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 16);
        var index = 0;
        foreach (var ch in template)
        {
            if (ch == '#' && index < values.Count)
            {
                builder.Append(FormatNumber(values[index++]));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string FormatNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AddIfPositive(List<string> lines, string keyword, int value)
    {
        if (value > 0)
        {
            lines.Add(keyword + ": " + value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>PoB 的 Rarity 行要全大写。</summary>
    private static string RarityText(Rarity rarity) => rarity switch
    {
        Rarity.Normal => "NORMAL",
        Rarity.Magic => "MAGIC",
        Rarity.Rare => "RARE",
        Rarity.Unique => "UNIQUE",
        Rarity.Gem => "GEM",
        Rarity.Currency => "CURRENCY",
        _ => "RARE",
    };
}
