using Sidekick.Game.TradeStats;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 词缀搜索器的过滤规则：只留「当前这副标签集里能出的」词缀。
///
/// 三条降级路径都返回原样（<paramref name="index"/> 为 null = 索引建不起来，
/// 标签集为空 = 没有装备上下文 / 认不出部位）：过滤可以关，但绝不能静默把库清空。
/// </summary>
public static class AffixSearchFilter
{
    public static List<TradeStatDefinition> Apply(
        IEnumerable<TradeStatDefinition> matched,
        AffixPoolIndex? index,
        IReadOnlyCollection<string>? tags)
    {
        if (index is null || tags is null || tags.Count == 0)
        {
            return [.. matched];
        }

        return [.. matched.Where(x => index.SpawnsOn(x.Id, x.Text, tags))];
    }
}
