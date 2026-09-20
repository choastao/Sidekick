using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Sidekick.Game;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;
using Xunit.Abstractions;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 期望成本的核对：池条数 / 池权重 / 目标条数 / 目标权重 / P / 期望次数。
///
/// 期望值是用 Python 直接从 <c>affix-pool-coe.json</c> 里按底材 + 侧别 + ilvl 建的池算出来的
/// （P = Σ命中权重 / Σ池内权重），逐条核对必须完全一致。
/// </summary>
public class AffixPoolChanceTests
{
    /// <summary>物品等级统一 80。</summary>
    private const int ItemLevel = 80;

    /// <summary>单次操作成本（神聖石）——只用来验算「期望成本 = 期望次数 × 单次成本」。</summary>
    private const double SingleOperationCost = 0.1154;

    private readonly ITestOutputHelper output;
    private readonly AffixWeightService weights = new(NullLogger<AffixWeightService>.Instance);
    private readonly AffixPoolCoEService coe = new(NullLogger<AffixPoolCoEService>.Instance);

    public AffixPoolChanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static IEnumerable<object[]> AcceptanceRows =>
    [
        // 场景 | 底材 id | 侧 | 目标词缀 | 目标值 | 池条数 | 池权重 | 目标条数 | 目标权重 | P% | 期望次数
        ["ilvl80 腰带 belt(3)", "3", AffixSide.Suffix, "to Fire Resistance", 10d, 85, 63262d, 7, 7000d, 11.07, 9.0],
        ["ilvl80 腰带 belt(3)", "3", AffixSide.Prefix, "to maximum Life", 10d, 61, 49608d, 10, 10000d, 20.16, 5.0],
        ["ilvl80 戒指 ring(1)", "1", AffixSide.Suffix, "to Fire Resistance", 10d, 114, 88865d, 7, 7000d, 7.88, 12.7],
        ["ilvl80 项链 amulet(2)", "2", AffixSide.Suffix, "to Fire Resistance", 10d, 143, 97420d, 7, 7000d, 7.19, 13.9],
        ["ilvl80 鞋子 boots DEX(40)", "40", AffixSide.Prefix, "to maximum Life", 10d, 43, 43000d, 9, 9000d, 20.93, 4.8],
        ["ilvl80 鞋子 boots DEX(40)", "40", AffixSide.Prefix, "Movement Speed", 10d, 43, 43000d, 5, 5000d, 11.63, 8.6],
        ["ilvl80 头盔 helmet DEX(53)", "53", AffixSide.Prefix, "to maximum Life", 10d, 59, 56200d, 16, 16000d, 28.47, 3.5],

        // 武器（CoE 里按具体类型分底材）。单手剑那份数据的权重恰好只有 0/1，
        // 池权重 = 池条数 —— 这不是特例，是数据的真实值，模型照样按权重算。
        ["ilvl80 单手剑 One Hand Sword(13)", "13", AffixSide.Prefix, "increased Physical Damage", 10d, 67, 67d, 14, 14d, 20.90, 4.8],
        ["ilvl80 弓 bow(20)", "20", AffixSide.Prefix, "increased Physical Damage", 10d, 72, 43758d, 14, 8950d, 20.45, 4.9],
        ["ilvl80 魔杖 wand(18)", "18", AffixSide.Prefix, "increased Spell Damage", 10d, 85, 41156d, 17, 8652d, 21.02, 4.8],
    ];

    [Theory]
    [MemberData(nameof(AcceptanceRows))]
    public void Pool_chance_matches_expected_table(
        string scenario,
        string baseId,
        AffixSide side,
        string pattern,
        double targetValue,
        int expectedPool,
        double expectedPoolWeight,
        int expectedTarget,
        double expectedTargetWeight,
        double expectedProbabilityPercent,
        double expectedTries)
    {
        Assert.True(coe.HasData, $"找不到 affix-pool-coe.json（查找路径：{string.Join(" | ", coe.SearchedPaths)}）");

        var pool = coe.GetPool(baseId, side, ItemLevel);
        var estimate = ExpectedCostCalculator.Estimate(pool, [pattern], targetValue, SingleOperationCost);

        output.WriteLine(
            "{0,-32} {1,-6} {2,-26} 池 {3,4} 权重 {4,7} 目标 {5,2} 权重 {6,6} P {7,6:0.00}% 平均 1/{8,5:0.0} 次",
            scenario,
            side,
            pattern,
            estimate.PoolCount,
            estimate.PoolWeight,
            estimate.TargetCount,
            estimate.TargetWeight,
            estimate.ProbabilityPercent,
            estimate.ExpectedTries);

        Assert.Equal(expectedPool, estimate.PoolCount);
        Assert.Equal(expectedPoolWeight, estimate.PoolWeight);
        Assert.Equal(expectedTarget, estimate.TargetCount);
        Assert.Equal(expectedTargetWeight, estimate.TargetWeight);
        Assert.Equal(expectedProbabilityPercent, Math.Round(estimate.ProbabilityPercent, 2));
        Assert.Equal(expectedTries, Math.Round(estimate.ExpectedTries, 1));

        // 期望成本 = 期望次数 × 单次操作成本
        Assert.Equal(
            Math.Round(estimate.ExpectedTries * SingleOperationCost, 6),
            Math.Round(estimate.ExpectedCost, 6));
    }

    [Fact]
    public void Tag_set_uses_slot_plus_armour_umbrella_plus_defence_subtype()
    {
        // 护甲槽位：槽位键 + armour 伞 + 防御值推出来的子类
        Assert.Equal(["boots", "armour", "dex_armour"], AffixPoolTags.Resolve("boots", 0, 120, 0).Tags);
        Assert.Equal(["helmet", "armour", "str_dex_int_armour"], AffixPoolTags.Resolve("helmet", 10, 20, 30).Tags);
        // 盾牌的防御子类键是 str_shield 这一族（不是 str_armour）——
        // 这条断言原来写死了 str_armour，把 bug 固化成了「预期行为」。审计（2026-09-19）抓出。
        Assert.Equal(["shield", "armour", "str_armour", "str_shield"], AffixPoolTags.Resolve("shield", 10, 0, 0).Tags);
        Assert.Equal(["shield", "armour", "str_dex_armour", "str_dex_shield"], AffixPoolTags.Resolve("shield", 10, 20, 0).Tags);
        Assert.Equal(["shield", "armour", "str_int_armour", "str_int_shield"], AffixPoolTags.Resolve("shield", 10, 0, 30).Tags);
        Assert.Equal(["focus", "int_armour"], AffixPoolTags.Resolve("focus", 0, 0, 30).Tags);

        // 首饰没有 armour 伞，也没有防御值子类（戒指/项链加 armour 会让池变大、P 偏小）
        var ring = AffixPoolTags.Resolve("ring", 0, 0, 0);
        Assert.Equal(["ring"], ring.Tags);
        Assert.False(ring.Degraded);
        Assert.Equal(["amulet"], AffixPoolTags.Resolve("amulet", 0, 0, 0).Tags);

        // 退化路径：护甲槽位拿不到防御值 → 只用 {槽位, armour}，并且明确标成偏乐观
        var degraded = AffixPoolTags.Resolve("boots", 0, 0, 0);
        Assert.Equal(["boots", "armour"], degraded.Tags);
        Assert.True(degraded.Degraded);
    }

    [Fact]
    public void Weapon_tags_use_type_plus_umbrella_plus_handedness()
    {
        // 剑/斧/锤在权重表里只有 sword/axe/mace 一个键，不区分单手双手，
        // 所以单手还是双手要靠物品类别补 —— 这正是 weapon 那把伞之外还要 handedness 的原因。
        Assert.Equal(
            ["sword", "weapon", "one_hand_weapon"],
            AffixPoolTags.Resolve("sword", 0, 0, 0, ItemClass.OneHandSword).Tags);
        Assert.Equal(
            ["sword", "weapon", "two_hand_weapon"],
            AffixPoolTags.Resolve("sword", 0, 0, 0, ItemClass.TwoHandSword).Tags);

        // 类型键本身就能定下单手双手的（魔杖单手、弓双手）
        Assert.Equal(
            ["wand", "weapon", "one_hand_weapon"],
            AffixPoolTags.Resolve("wand", 0, 0, 0, ItemClass.Wand).Tags);
        Assert.Equal(
            ["bow", "weapon", "two_hand_weapon"],
            AffixPoolTags.Resolve("bow", 0, 0, 0, ItemClass.Bow).Tags);

        // 剑/斧/锤是单手双手共用键：拿不到类别时不猜，只给 {类型, weapon}。
        // 这会让池偏小、P 偏大、成本偏乐观 —— 但它只在类别解析不出来时发生，
        // 面板走的是带类别的重载，拿得到就是精确的。
        Assert.Equal(["sword", "weapon"], AffixPoolTags.Resolve("sword", 0, 0, 0).Tags);
        Assert.Equal(["axe", "weapon"], AffixPoolTags.Resolve("axe", 0, 0, 0).Tags);
        Assert.Equal(["mace", "weapon"], AffixPoolTags.Resolve("mace", 0, 0, 0).Tags);

        // 武器不该带上 armour 伞
        Assert.DoesNotContain(AffixPoolTags.Armour, AffixPoolTags.Resolve("wand", 0, 0, 0, ItemClass.Wand).Tags);
    }

    [Fact]
    public void Tag_set_comes_from_the_parsed_item()
    {
        // 面板走的就是这条路：物品类别 + 物品上解析出来的防御值，不查基底名。
        var boots = new Item(GameType.Poe2, new OriginalText("Rarity: Rare\nTest Boots"))
        {
            ItemClass = new ItemClassDefinition { Id = "Boots", Type = ItemClass.Boots },
        };
        boots.Properties.EvasionRating = 150;

        var resolved = AffixPoolTags.Resolve(boots, SlotKeys.Boots);
        Assert.Equal(["boots", "armour", "dex_armour"], resolved.Tags);
        Assert.False(resolved.Degraded);

        // 防御值读不到（全 0）→ 退化路径 + 偏乐观标记
        var unknown = new Item(GameType.Poe2, new OriginalText("Rarity: Rare\nTest Boots"))
        {
            ItemClass = new ItemClassDefinition { Id = "Boots", Type = ItemClass.Boots },
        };

        var degraded = AffixPoolTags.Resolve(unknown, SlotKeys.Boots);
        Assert.Equal(["boots", "armour"], degraded.Tags);
        Assert.True(degraded.Degraded);

        // 戒指：没有 armour 伞（加上会让池变大、P 偏小）
        var ring = new Item(GameType.Poe2, new OriginalText("Rarity: Rare\nTest Ring"))
        {
            ItemClass = new ItemClassDefinition { Id = "Ring", Type = ItemClass.Ring },
        };

        Assert.Equal(["ring"], AffixPoolTags.Resolve(ring, SlotKeys.Ring1).Tags);
    }

    [Fact]
    public void Reroll_uses_uniform_distribution_inside_the_current_tier()
    {
        // 当前 tier 60-69、目标 65：P = (69-65+1) / (69-60+1) = 5/10
        var estimate = ExpectedCostCalculator.EstimateReroll(60, 69, 65, 0.2);

        Assert.Equal(10, estimate.PoolCount);
        Assert.Equal(5, estimate.TargetCount);
        Assert.Equal(50d, estimate.ProbabilityPercent);
        Assert.Equal(2d, estimate.ExpectedTries, 6);
        Assert.Equal(0.4d, estimate.ExpectedCost, 6);

        // 目标 ≤ 区间下限：任何一次重掷都够
        Assert.Equal(100d, ExpectedCostCalculator.EstimateReroll(60, 69, 55, 0.2).ProbabilityPercent);

        // 区间解析不出来：不给概率，也不给期望成本
        var unknown = ExpectedCostCalculator.EstimateReroll(double.NaN, double.NaN, 65, 0.2);
        Assert.False(unknown.IsUsable);
        Assert.Equal(double.PositiveInfinity, unknown.ExpectedCost);
    }

    [Fact]
    public void Target_pattern_matching_ignores_numbers_and_punctuation()
    {
        Assert.Equal("addstofiredamage", ModWeight.Normalize("Adds # to # Fire Damage"));
        Assert.Equal("addstofiredamage", ModWeight.Normalize("Adds (5-8) to (11-14) Fire Damage"));
        Assert.Equal("tomaximumlife", ModWeight.Normalize("+(70-84) to maximum Life"));

        // 数值上限取括号区间的上界；没有括号的单值取最大数字
        Assert.Equal(84d, new ModWeight { Text = "+(70-84) to maximum Life" }.UpperBound);
        Assert.Equal(30d, new ModWeight { Text = "30% increased Movement Speed" }.UpperBound);
        Assert.Null(new ModWeight { Text = "Cannot be Frozen" }.UpperBound);
    }

    [Fact]
    public void Pool_only_contains_mods_that_can_spawn_on_the_tag_set()
    {
        var tags = new[] { AffixPoolTags.Boots, AffixPoolTags.Armour, "dex_armour" };
        var pool = weights.GetPool(tags, AffixSide.Prefix, ItemLevel);

        // 权重只有 0/1：default 恒 0，绝不能拿来兜底（袋子漏了它 → 池会多出一堆出不来的词缀）
        Assert.NotEmpty(pool);
        Assert.All(pool, mod => Assert.True(
            mod.SpawnsOn(tags),
            string.Format(CultureInfo.InvariantCulture, "{0} 在标签集里没有任何权重 > 0 的键", mod.Id)));
        Assert.All(pool, mod => Assert.True(
            mod.Level <= ItemLevel,
            string.Format(CultureInfo.InvariantCulture, "{0} 的 ilvl {1} 高于物品等级 {2}", mod.Id, mod.Level, ItemLevel)));
        Assert.All(pool, mod => Assert.Equal(AffixSide.Prefix, mod.Side));
    }

    /// <summary>
    /// 面板上的概率文案全部走 string.Format：占位符和参数个数对不上会在渲染时直接抛异常。
    /// 这里把中英两份资源都按「最大占位符」格式化一遍，顺带确认两份的占位符签名一致。
    /// </summary>
    [Fact]
    public void Probability_strings_have_matching_placeholders()
    {
        var manager = new ResourceManager(
            "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
            typeof(BuildTargetResources).Assembly);

        string[] keys =
        [
            "Craft_Prob_Title",
            "Craft_Prob_Pool",
            "Craft_Prob_Rate",
            "Craft_Prob_Expected_Cost",
            "Craft_Prob_Expected_Cost_Chaos",
            "Craft_Prob_Reroll_Range",
            "Craft_Prob_Reroll_Pool",
            "Craft_Prob_Total_Title",
            "Craft_Prob_Total",
            "Craft_Prob_Total_Optional",
            "Craft_Prob_Total_None",
            "Craft_Prob_Source_Data",
            "Craft_Prob_Note_Source",
            "Craft_Prob_Note_Mean",
            "Craft_Prob_Note_Chaos",
            "Craft_Prob_No_Weights",
            "Craft_Prob_Weights_Hint",
            "Craft_Prob_Weights_Load_Failed",
            "Craft_Prob_No_Tags",
            "Craft_Prob_No_Base",
            "Craft_Prob_No_Item_Level",
            "Craft_Prob_No_Side",
            "Craft_Prob_No_Tier",
            "Craft_Prob_No_Target",
            "Craft_Prob_Target_Too_High",
            "Craft_Prob_Value_Unknown",
        ];

        foreach (var key in keys)
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture);
            var chinese = manager.GetString(key, CultureInfo.GetCultureInfo("zh"));

            Assert.False(string.IsNullOrEmpty(english), $"{key} 缺少英文文案");
            Assert.False(string.IsNullOrEmpty(chinese), $"{key} 缺少中文文案");

            // 两种语言的占位符必须一样，否则切语言时才会炸
            Assert.Equal(Signature(english!), Signature(chinese!));

            var formatted = string.Format(
                CultureInfo.InvariantCulture,
                english!,
                Enumerable.Range(0, Signature(english!))
                          .Select(index => (object)(index + 1))
                          .ToArray());

            Assert.DoesNotContain("{", formatted, StringComparison.Ordinal);
        }
    }

    /// <summary>文案里用到的参数个数（最大占位符下标 + 1）。</summary>
    private static int Signature(string format)
    {
        var matches = Regex.Matches(format, @"\{(\d+)");
        return matches.Count == 0 ? 0 : matches.Max(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
    }
}
