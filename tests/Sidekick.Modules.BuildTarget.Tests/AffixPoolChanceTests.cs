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
/// 期望成本的核对：池条数 / 目标条数 / P / 期望次数。
///
/// 期望值是用 Python 按「标签集 = {槽位键} ∪ {armour?} ∪ {子类?}」+「权重只有 0/1」+「目标值 10
/// （这三条目标词缀池内任何一个 tier 的上限都 ≥ 10，等价于「不挑 tier」）」算出来的，逐条核对必须完全一致。
/// </summary>
public class AffixPoolChanceTests
{
    /// <summary>物品等级统一 80。</summary>
    private const int ItemLevel = 80;

    /// <summary>目标数值下限（见类注释）。</summary>
    private const double TargetValue = 10;

    /// <summary>单次操作成本（神聖石）——只用来验算「期望成本 = 期望次数 × 单次成本」。</summary>
    private const double SingleOperationCost = 0.1154;

    private readonly ITestOutputHelper output;
    private readonly AffixWeightService weights = new(NullLogger<AffixWeightService>.Instance);

    public AffixPoolChanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static IEnumerable<object[]> AcceptanceRows =>
    [
        // 场景 | 槽位键 | 护甲 | 闪避 | 能量护盾 | 侧 | 目标词缀 | 池条数 | 目标条数 | P% | 期望次数
        ["ilvl80 鞋子 dex_armour", AffixPoolTags.Boots, 0, 100, 0, AffixSide.Prefix, "to maximum Life", 65, 15, 23.08, 4.3],
        ["ilvl80 鞋子 dex_armour", AffixPoolTags.Boots, 0, 100, 0, AffixSide.Suffix, "to Fire Resistance", 81, 7, 8.64, 11.6],
        ["ilvl80 鞋子 dex_armour", AffixPoolTags.Boots, 0, 100, 0, AffixSide.Prefix, "Movement Speed", 65, 5, 7.69, 13.0],
        ["ilvl80 头盔 dex_armour", AffixPoolTags.Helmet, 0, 100, 0, AffixSide.Prefix, "to maximum Life", 74, 16, 21.62, 4.6],
        ["ilvl80 戒指（无 armour 标签）", AffixPoolTags.Ring, 0, 0, 0, AffixSide.Suffix, "to Fire Resistance", 99, 7, 7.07, 14.1],
        ["ilvl80 戒指（无 armour 标签）", AffixPoolTags.Ring, 0, 0, 0, AffixSide.Prefix, "to maximum Life", 100, 8, 8.00, 12.5],
        ["ilvl80 项链（无 armour 标签）", AffixPoolTags.Amulet, 0, 0, 0, AffixSide.Suffix, "to Fire Resistance", 123, 7, 5.69, 17.6],
        ["ilvl80 鞋子退化路径（只用 {boots,armour}）", AffixPoolTags.Boots, 0, 0, 0, AffixSide.Prefix, "to maximum Life", 23, 9, 39.13, 2.6],

        // 武器：标签集 = {具体类型键} ∪ {weapon 伞} ∪ {单手/双手伞}
        // （这里不传类别，走「按类型键推断单手双手」的兜底分支）
        ["ilvl80 魔杖 wand", "wand", 0, 0, 0, AffixSide.Prefix, "increased Spell Damage", 116, 8, 6.90, 14.5],
        ["ilvl80 魔杖 wand", "wand", 0, 0, 0, AffixSide.Suffix, "increased Cast Speed", 146, 7, 4.79, 20.9],
        ["ilvl80 弓 bow", "bow", 0, 0, 0, AffixSide.Prefix, "increased Physical Damage", 78, 7, 8.97, 11.1],
        // 剑/斧/锤的单手双手要靠物品类别才能定；不传类别时按「不猜」处理，
        // 标签集只有 {sword, weapon}，池比带 handedness 时小（77 vs 91）。
        ["ilvl80 剑 sword（无类别，不猜单手双手）", "sword", 0, 0, 0, AffixSide.Prefix, "increased Physical Damage", 77, 7, 9.09, 11.0],
    ];

    [Theory]
    [MemberData(nameof(AcceptanceRows))]
    public void Pool_chance_matches_expected_table(
        string scenario,
        string slotKey,
        int armour,
        int evasion,
        int energyShield,
        AffixSide side,
        string pattern,
        int expectedPool,
        int expectedTarget,
        double expectedProbabilityPercent,
        double expectedTries)
    {
        Assert.True(weights.HasData, $"找不到 affix-weights.json（查找路径：{string.Join(" | ", weights.SearchedPaths)}）");

        var tags = AffixPoolTags.Resolve(slotKey, armour, evasion, energyShield);
        var pool = weights.GetPool(tags.Tags, side, ItemLevel);
        var estimate = ExpectedCostCalculator.Estimate(pool, [pattern], TargetValue, SingleOperationCost);

        output.WriteLine(
            "{0,-36} {1,-6} {2,-22} 池 {3,4} 目标 {4,2} P {5,6:0.00}% 平均 1/{6,5:0.0} 次  [标签 {7}]",
            scenario,
            side,
            pattern,
            estimate.PoolCount,
            estimate.TargetCount,
            estimate.ProbabilityPercent,
            estimate.ExpectedTries,
            tags.TagText);

        Assert.Equal(expectedPool, estimate.PoolCount);
        Assert.Equal(expectedTarget, estimate.TargetCount);
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
        Assert.Equal(["shield", "armour", "str_armour"], AffixPoolTags.Resolve("shield", 10, 0, 0).Tags);
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
            "Craft_Prob_Note_Degraded",
            "Craft_Prob_Note_Degraded_Short",
            "Craft_Prob_Tags",
            "Craft_Prob_No_Weights",
            "Craft_Prob_Weights_Hint",
            "Craft_Prob_Weights_Load_Failed",
            "Craft_Prob_No_Tags",
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
