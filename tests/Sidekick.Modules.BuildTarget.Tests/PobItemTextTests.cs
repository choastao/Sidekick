using Sidekick.Game;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.ItemDefinitions;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Parser.Stats;
using Sidekick.Game.Stats;
using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 中文物品 → PoB 英文 raw 文本。断言的都是"引擎真的认不认"这件事：
/// 基底必须是英文（否则 PoB 会当成空物品，试穿结果=没变化，是假结论）、
/// 词缀要能查到英文模板、查不到的条数要如实报出来。
/// </summary>
public class PobItemTextTests
{
    private static readonly Dictionary<string, string> InvariantStats = new()
    {
        ["explicit.stat_fire_res"] = "+#% to Fire Resistance",
        ["explicit.stat_life"] = "+# to maximum Life",
        ["implicit.stat_es"] = "+# to maximum Energy Shield",
    };

    private static Item Helmet()
    {
        var item = new Item(GameType.Poe2, new OriginalText("Rarity: Rare\n测试头盔"))
        {
            Name = "催眠之冠",
            Type = "破舊兜帽",
            ItemClass = new ItemClassDefinition { Id = "Helmet", Type = ItemClass.Helmet },
            InvariantTradeItem = new TradeItem { Name = "Hypnotic Corona", Type = "Kamasan Tiara" },
        };

        item.Properties.Rarity = Rarity.Rare;
        item.Properties.EnergyShield = 436;
        item.Properties.ItemLevel = 82;
        item.Properties.Quality = 20;
        return item;
    }

    private static Stat Stat(StatCategory category, string text, double[] values, string? tradeId, string definitionText)
    {
        return new Stat(category, text)
        {
            Definitions =
            [
                new StatDefinition
                {
                    Text = definitionText,
                    TradeIds = tradeId == null ? null : [tradeId],
                },
            ],
            Values = [.. values],
        };
    }

    [Fact]
    public void Uses_invariant_names_and_english_affixes()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));

        var result = PobItemText.Build(item, InvariantStats);

        Assert.True(result.BaseIdentified);
        Assert.Equal(2, result.TotalStats);
        Assert.Equal(2, result.MappedStats);
        Assert.Equal(0, result.Skipped);

        var lines = result.Text.Split('\n').Select(x => x.Trim()).ToList();
        Assert.Contains("Rarity: RARE", lines);
        Assert.Contains("Hypnotic Corona", lines);          // 英文名，不是「催眠之冠」
        Assert.Contains("Kamasan Tiara", lines);             // 英文基底，不是「破舊兜帽」
        Assert.Contains("Energy Shield: 436", lines);
        Assert.Contains("Item Level: 82", lines);
        Assert.Contains("+45% to Fire Resistance", lines);   // # 已被数值替换
        Assert.Contains("+88 to maximum Life", lines);
        Assert.DoesNotContain(lines, x => x.Contains('#'));  // 不该留下占位符
    }

    [Fact]
    public void Implicits_are_declared_before_explicit_ones()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));
        item.Stats.Add(Stat(StatCategory.Implicit, "+30 最大能量护盾", [30], "implicit.stat_es", "+# 最大能量护盾"));

        var text = PobItemText.Build(item, InvariantStats).Text;
        var implicitIndex = text.IndexOf("Implicits: 1", StringComparison.Ordinal);
        var implicitLine = text.IndexOf("+30 to maximum Energy Shield", StringComparison.Ordinal);
        var explicitLine = text.IndexOf("+45% to Fire Resistance", StringComparison.Ordinal);

        Assert.True(implicitIndex >= 0, "应有 Implicits: 1 声明");
        Assert.True(implicitLine > implicitIndex, "隐式词缀要排在 Implicits 声明之后");
        Assert.True(explicitLine > implicitLine, "显式词缀排在隐式之后");
    }

    [Fact]
    public void Unknown_affixes_are_counted_not_silently_dropped()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));
        // 引擎表里没有这条 id → 必须计入 Skipped
        item.Stats.Add(Stat(StatCategory.Explicit, "+3 某条奇怪词缀", [3], "explicit.stat_unknown", "+# 某条奇怪词缀"));

        var result = PobItemText.Build(item, InvariantStats);

        Assert.Equal(2, result.TotalStats);
        Assert.Equal(1, result.MappedStats);
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain("奇怪词缀", result.Text);
    }

    [Fact]
    public void Pseudo_stats_are_not_treated_as_affixes()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));
        // 伪属性是 Sidekick 自己算的合计，不是装备上的词缀 —— 给 PoB 只添乱
        item.Stats.Add(Stat(StatCategory.Pseudo, "总计 +45% 火焰抗性", [45], "pseudo.stat_total_fire_res", "+#% 总计火焰抗性"));

        var result = PobItemText.Build(item, InvariantStats);

        Assert.Equal(1, result.TotalStats);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Missing_english_base_is_reported_not_guessed()
    {
        var item = Helmet();
        item.InvariantTradeItem = null;
        item.InvariantDefinition = null!;
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));

        var result = PobItemText.Build(item, InvariantStats);

        // 基底取不到英文：调用方必须据此拒绝试穿（否则 PoB 会把中文基底当空物品 →
        // 试穿结果 = 与基线相同 = 假装"这件装备没有影响"）
        Assert.False(result.BaseIdentified);
        Assert.Contains("破舊兜帽", result.Text);
    }
}
