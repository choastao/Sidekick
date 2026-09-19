using System.Globalization;
using System.Text.RegularExpressions;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Stats;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 把一件装备的 <see cref="Item.Stats"/> 折算成抗性 / 属性贡献。
///
/// 认的是词缀 id（StatDefinition.TradeIds 里的 stat_xxxxxxxxxx），不是物品原文：
/// id 是游戏给的、和客户端语言无关，中文客户端和英文客户端算出来完全一样；
/// 同一件装备的数值也来自解析器已经拆好的结果，这里不再正则解析物品原文。
///
/// 只收「+X% 抗性 / +X 属性」这类直接相加的词缀：
/// 「提高 X% 抗性」是乘法词缀，不知道角色基础值就算不准，一律不计。
/// </summary>
public static class CandidateBasketCalculator
{
    /// <summary>
    /// 词缀 id → 它给哪些指标加值。
    /// 键是 trade id 去掉类别前缀（explicit. / crafted. / implicit. / rune. …）后的部分，
    /// 这一段在 PoE1 / PoE2 里是同一套编号，所以一张表两个游戏都能用。
    /// </summary>
    private static readonly Dictionary<string, BasketStat[]> StatIds = new(StringComparer.Ordinal)
    {
        // 单抗
        ["stat_3372524247"] = [BasketStat.FireResistance],
        ["stat_4220027924"] = [BasketStat.ColdResistance],
        ["stat_1671376347"] = [BasketStat.LightningResistance],
        ["stat_2923486259"] = [BasketStat.ChaosResistance],

        // +X% 全部元素抗性：火 / 冰 / 电 各计一次（界面上写明了，否则用户对不上数）
        ["stat_2901986750"] = [BasketStat.FireResistance, BasketStat.ColdResistance, BasketStat.LightningResistance],

        // +X% 全部抗性（含混沌）：PoE1 / PoE2 各一个 id
        ["stat_2016723660"] = [BasketStat.FireResistance, BasketStat.ColdResistance, BasketStat.LightningResistance, BasketStat.ChaosResistance],
        ["stat_3128852541"] = [BasketStat.FireResistance, BasketStat.ColdResistance, BasketStat.LightningResistance, BasketStat.ChaosResistance],

        // 双抗：两条各计一次
        ["stat_2915988346"] = [BasketStat.FireResistance, BasketStat.ColdResistance],
        ["stat_4277795662"] = [BasketStat.ColdResistance, BasketStat.LightningResistance],
        ["stat_3441501978"] = [BasketStat.FireResistance, BasketStat.LightningResistance],
        ["stat_378817135"] = [BasketStat.FireResistance, BasketStat.ChaosResistance],
        ["stat_3393628375"] = [BasketStat.ColdResistance, BasketStat.ChaosResistance],
        ["stat_3465022881"] = [BasketStat.LightningResistance, BasketStat.ChaosResistance],

        // 属性
        ["stat_4080418644"] = [BasketStat.Strength],
        ["stat_3261801346"] = [BasketStat.Dexterity],
        ["stat_328541901"] = [BasketStat.Intelligence],

        // +X 全部能力值：三条各计一次
        ["stat_1379411836"] = [BasketStat.Strength, BasketStat.Dexterity, BasketStat.Intelligence],
        ["stat_2897413282"] = [BasketStat.Strength, BasketStat.Dexterity, BasketStat.Intelligence],
    };

    private static readonly Regex IdPrefix = new(@"^[a-z]+\.", RegexOptions.Compiled);

    /// <summary>一件装备的抗性 / 属性贡献。</summary>
    public static BasketContribution Contribute(Item? item)
    {
        var result = new BasketContribution();
        if (item?.Stats == null)
        {
            return result;
        }

        foreach (var stat in item.Stats)
        {
            var applied = false;

            // 按「词缀定义」逐条取数：同一条词缀行可能对应多个定义，
            // 每个定义有自己的数值来源，这样不会把一条的值算到另一条头上。
            foreach (var definition in stat.Definitions)
            {
                var stats = Resolve(definition.TradeIds);
                if (stats.Length == 0)
                {
                    continue;
                }

                if (!TryGetValue(stat, definition, out var value))
                {
                    continue;
                }

                foreach (var target in stats)
                {
                    result.Add(target, value);
                }

                applied = true;
            }

            // 定义里既没有写死数值、也没有可用的 Pattern（罕见）时，退回解析器算好的数值。
            // 只补一次，避免同一条词缀被多个定义重复计。
            if (applied)
            {
                continue;
            }

            var fallback = Resolve(stat.Definitions.SelectMany(x => x.TradeIds ?? []));
            if (fallback.Length == 0 || stat.Values.Count == 0)
            {
                continue;
            }

            foreach (var target in fallback)
            {
                result.Add(target, stat.AverageValue);
            }
        }

        return result;
    }

    /// <summary>把一批备选加总（只算勾选上的）。</summary>
    public static BasketContribution Sum(IEnumerable<CandidateBasketItem> items)
    {
        var total = new BasketContribution();
        foreach (var item in items)
        {
            if (!item.Enabled)
            {
                continue;
            }

            total.Add(item.Contribution);
        }

        return total;
    }

    /// <summary>把一批词缀 id 映射成要计值的指标（去重）。</summary>
    private static BasketStat[] Resolve(IEnumerable<string>? tradeIds)
    {
        if (tradeIds == null)
        {
            return [];
        }

        List<BasketStat>? stats = null;
        foreach (var tradeId in tradeIds)
        {
            if (string.IsNullOrWhiteSpace(tradeId))
            {
                continue;
            }

            // trade id 形如 explicit.stat_3372524247，也可能带参数后缀（...|32151）
            var id = tradeId.Split('|')[0];
            id = IdPrefix.Replace(id, string.Empty);
            if (!StatIds.TryGetValue(id, out var mapped))
            {
                continue;
            }

            stats ??= [];
            foreach (var stat in mapped)
            {
                if (!stats.Contains(stat))
                {
                    stats.Add(stat);
                }
            }
        }

        return stats?.ToArray() ?? [];
    }

    /// <summary>
    /// 取这条词缀定义的数值：优先写死的值，其次按定义自己的 Pattern 从词缀行里取（和解析器同一套规则）。
    /// </summary>
    private static bool TryGetValue(Stat stat, StatDefinition definition, out double value)
    {
        value = 0;

        if (definition.Value.HasValue)
        {
            value = definition.Value.Value;
            return true;
        }

        if (definition.Pattern == null)
        {
            return false;
        }

        var match = definition.Pattern.Match(stat.Text);
        if (!match.Success)
        {
            return false;
        }

        var sum = 0d;
        var found = false;

        // 第 0 组是整行文本，从第 1 组开始才是数值
        for (var index = 1; index < match.Groups.Count; index++)
        {
            foreach (Capture capture in match.Groups[index].Captures)
            {
                if (!double.TryParse(capture.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    continue;
                }

                sum += definition.Negate ? -parsed : parsed;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        value = sum;
        return true;
    }
}
