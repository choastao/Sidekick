using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using Sidekick.Game;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 真实权重模型（affix-pool-coe.json，Craft of Exile 数据）的验收：
/// 池按**底材**建（不是按标签集），概率按 Σ命中权重 / Σ池内权重 算（不是条数比）。
///
/// 这里的数字全部是实测值（腰带 ilvl 82：后缀 94 档 / 70712 权重、前缀 61 档 / 49608 权重，
/// 与 poe2db 的池总权重交叉验证一致），出现偏差就说明权重算错了。
/// </summary>
public class AffixPoolCoeTests
{
    /// <summary>腰带（底材 id 3）。</summary>
    private const string Belt = "3";

    private const int ItemLevel = 82;

    /// <summary>单次操作成本（神聖石），只用来验算「期望成本 = 期望次数 × 单次成本」。</summary>
    private const double SingleOperationCost = 0.1154;

    private readonly AffixPoolCoEService coe = new(NullLogger<AffixPoolCoEService>.Instance);

    [Fact]
    public void Belt_suffix_pool_is_94_tiers_worth_70712_weight()
    {
        var pool = coe.GetPool(Belt, AffixSide.Suffix, ItemLevel);

        Assert.Equal(94, pool.Count);
        Assert.Equal(70712d, pool.Sum(x => x.Weight));

        // 池是按底材取的：权重不同的族必须各自带自己的权重，不能被汇总成条数。
        Assert.Contains(pool, x => x.Weight is > 0 and not 1);
    }

    [Fact]
    public void Belt_prefix_pool_is_61_tiers_worth_49608_weight()
    {
        var pool = coe.GetPool(Belt, AffixSide.Prefix, ItemLevel);

        Assert.Equal(61, pool.Count);
        Assert.Equal(49608d, pool.Sum(x => x.Weight));
    }

    [Fact]
    public void Fire_resistance_at_value_41_hits_one_tier_of_weight_1000()
    {
        var pool = coe.GetPool(Belt, AffixSide.Suffix, ItemLevel);
        var estimate = ExpectedCostCalculator.Estimate(pool, ["to Fire Resistance"], 41d, SingleOperationCost);

        // 只有 FireResistance 的 ilvl=82 那一档（41-45）够得着目标 41。
        Assert.Equal(1, estimate.TargetCount);
        Assert.Equal(1000d, estimate.TargetWeight);
        Assert.Equal(70712d, estimate.PoolWeight);
        Assert.True(
            Math.Abs(estimate.Probability - 1000d / 70712d) <= 1e-6,
            $"概率应当是 1000/70712，实际 {estimate.Probability:R}");
        Assert.Equal(1000d / 70712d, estimate.Probability, 6);

        // 期望成本 = 期望次数 × 单次成本（P 就是上面那个加权比）。
        Assert.Equal(1d / (1000d / 70712d), estimate.ExpectedTries, 6);
        Assert.Equal(estimate.ExpectedTries * SingleOperationCost, estimate.ExpectedCost, 6);
    }

    [Fact]
    public void Target_without_a_value_sums_the_whole_family_weight()
    {
        var pool = coe.GetPool(Belt, AffixSide.Suffix, ItemLevel);

        // 只要出火抗、不限数值：火抗 8 档 × 1000 = 8000。
        var estimate = ExpectedCostCalculator.Estimate(pool, ["to Fire Resistance"], 0d, SingleOperationCost);

        Assert.Equal(8, estimate.TargetCount);
        Assert.Equal(8000d, estimate.TargetWeight);
        Assert.Equal(8000d / 70712d, estimate.Probability, 6);
    }

    [Fact]
    public void Low_weight_family_keeps_its_real_weight()
    {
        // 混沌抗是低权重族（250，常规词缀是 1000）。这条断言专门反证
        //「读的是真实权重」而不是「退化成数条数」—— 数条数的话这里会是 1。
        var pool = coe.GetPool(Belt, AffixSide.Suffix, ItemLevel);
        var chaos = pool.Where(x => x.Family == "ChaosResistance").ToList();

        Assert.NotEmpty(chaos);
        Assert.All(chaos, x => Assert.Equal(250d, x.Weight));

        var estimate = ExpectedCostCalculator.Estimate(pool, ["to Chaos Resistance"], 0d, SingleOperationCost);
        Assert.Equal(chaos.Count * 250d, estimate.TargetWeight);
    }

    [Fact]
    public void Probability_uses_weights_not_counts()
    {
        // 同一个池：条数比 1/94 = 1.06%，权重比 1000/70712 = 1.41%，两者必须不同 ——
        // 这条用来锁住「概率是加权求和」，防止有人改回数条数。
        var pool = coe.GetPool(Belt, AffixSide.Suffix, ItemLevel);
        var estimate = ExpectedCostCalculator.Estimate(pool, ["to Fire Resistance"], 41d, SingleOperationCost);

        Assert.NotEqual((double)estimate.TargetCount / estimate.PoolCount, estimate.Probability);
        Assert.Equal(estimate.TargetWeight / estimate.PoolWeight, estimate.Probability, 12);
    }

    [Fact]
    public void Empty_pool_and_unknown_base_are_safe()
    {
        // 底材 id 认不出来（null / 空 / 数据里没有）→ 空池，不抛异常。
        Assert.Empty(coe.GetPool(null, AffixSide.Suffix, ItemLevel));
        Assert.Empty(coe.GetPool("", AffixSide.Suffix, ItemLevel));
        Assert.Empty(coe.GetPool("999999", AffixSide.Suffix, ItemLevel));
        Assert.Empty(coe.GetPool(Belt, AffixSide.Unknown, ItemLevel));
        Assert.Empty(coe.GetPool(Belt, AffixSide.Suffix, 0));

        // 空池也要能走完概率计算：P = 0，不除零。
        var empty = ExpectedCostCalculator.Estimate([], ["to Fire Resistance"], 41d, SingleOperationCost);
        Assert.Equal(0, empty.PoolCount);
        Assert.Equal(0, empty.TargetCount);
        Assert.Equal(0d, empty.Probability);
        Assert.Equal(0d, empty.PoolWeight);
        Assert.False(empty.IsUsable);
        Assert.Equal(double.PositiveInfinity, empty.ExpectedCost);

        var nullPool = ExpectedCostCalculator.Estimate(null, ["to Fire Resistance"], 41d, SingleOperationCost);
        Assert.Equal(0d, nullPool.Probability);

        // 池里权重合计为 0 的极端情况（数据全 0）也不除零。
        var zeroWeight = ExpectedCostCalculator.Estimate(
            [new AffixPoolEntry { Text = "+#% to Fire Resistance", Max = 45, Weight = 0 }],
            ["to Fire Resistance"],
            41d,
            SingleOperationCost);
        Assert.Equal(0d, zeroWeight.Probability);
    }

    [Fact]
    public void GetPool_only_keeps_tiers_at_or_below_the_item_level()
    {
        var pool = coe.GetPool(Belt, AffixSide.Suffix, ItemLevel);

        Assert.All(pool, x => Assert.True(x.ItemLevel <= ItemLevel));
        Assert.All(pool, x => Assert.True(x.Weight > 0));
        Assert.All(pool, x => Assert.Equal(AffixSide.Suffix, x.Side));

        // ilvl 82 的腰带池比 ilvl 1 的大得多（档位是随物品等级放出来的）。
        Assert.True(pool.Count > coe.GetPool(Belt, AffixSide.Suffix, 1).Count);
    }

    [Fact]
    public void Dataset_loads_with_the_craft_of_exile_source()
    {
        Assert.True(
            coe.HasData,
            $"affix-pool-coe.json 未载入（查找路径：{string.Join(" | ", coe.SearchedPaths)}，"
            + $"读取错误：{coe.LoadError ?? "无"}）");
        Assert.Contains("Craft of Exile", coe.Source, StringComparison.OrdinalIgnoreCase);
        Assert.True(coe.ModCount > 0);
        Assert.True(coe.BaseCount > 0);
    }

    [Fact]
    public void ResolveBase_maps_jewellery_and_offhand_slots()
    {
        Assert.Equal("3", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Belt), SlotKeys.Belt));
        Assert.Equal("1", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Ring), SlotKeys.Ring1));
        Assert.Equal("2", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Amulet), SlotKeys.Amulet));
        Assert.Equal("4", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Quiver), SlotKeys.Offhand));
        Assert.Equal("229", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Focus), SlotKeys.Offhand));
        Assert.Equal("244", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Talisman), null));

        // 类别拿不到时用界面槽位兜底（这几种首饰没有属性组合，兜得住）。
        Assert.Equal("3", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Unknown), SlotKeys.Belt));
        Assert.Equal("1", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Unknown), SlotKeys.Ring2));
    }

    [Fact]
    public void ResolveBase_maps_armour_slots_by_attribute_combination()
    {
        // 护甲部位的属性组合从防御值推导：顺序固定 str / dex / int。
        Assert.Equal("5", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Shield, armour: 100), SlotKeys.Offhand));
        Assert.Equal("6", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Shield, evasion: 100), SlotKeys.Offhand));
        Assert.Equal("8", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Shield, armour: 100, evasion: 100), SlotKeys.Offhand));
        Assert.Equal("9", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Shield, armour: 100, energyShield: 100), SlotKeys.Offhand));

        Assert.Equal("45", AffixPoolTags.ResolveBase(ItemOf(ItemClass.BodyArmour, armour: 100), SlotKeys.BodyArmour));
        Assert.Equal("46", AffixPoolTags.ResolveBase(ItemOf(ItemClass.BodyArmour, evasion: 100), SlotKeys.BodyArmour));
        Assert.Equal("47", AffixPoolTags.ResolveBase(ItemOf(ItemClass.BodyArmour, energyShield: 100), SlotKeys.BodyArmour));
        Assert.Equal("48", AffixPoolTags.ResolveBase(ItemOf(ItemClass.BodyArmour, armour: 100, evasion: 100), SlotKeys.BodyArmour));
        Assert.Equal("49", AffixPoolTags.ResolveBase(ItemOf(ItemClass.BodyArmour, armour: 100, energyShield: 100), SlotKeys.BodyArmour));
        Assert.Equal("50", AffixPoolTags.ResolveBase(ItemOf(ItemClass.BodyArmour, evasion: 100, energyShield: 100), SlotKeys.BodyArmour));

        Assert.Equal("39", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots, armour: 100), SlotKeys.Boots));
        Assert.Equal("40", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots, evasion: 100), SlotKeys.Boots));
        Assert.Equal("41", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots, energyShield: 100), SlotKeys.Boots));
        Assert.Equal("42", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots, armour: 100, evasion: 100), SlotKeys.Boots));
        Assert.Equal("43", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots, armour: 100, energyShield: 100), SlotKeys.Boots));
        Assert.Equal("44", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots, evasion: 100, energyShield: 100), SlotKeys.Boots));

        Assert.Equal("33", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Gloves, armour: 100), SlotKeys.Gloves));
        Assert.Equal("34", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Gloves, evasion: 100), SlotKeys.Gloves));
        Assert.Equal("35", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Gloves, energyShield: 100), SlotKeys.Gloves));
        Assert.Equal("36", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Gloves, armour: 100, evasion: 100), SlotKeys.Gloves));
        Assert.Equal("37", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Gloves, armour: 100, energyShield: 100), SlotKeys.Gloves));
        Assert.Equal("38", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Gloves, evasion: 100, energyShield: 100), SlotKeys.Gloves));

        Assert.Equal("52", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet, armour: 100), SlotKeys.Helmet));
        Assert.Equal("53", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet, evasion: 100), SlotKeys.Helmet));
        Assert.Equal("54", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet, energyShield: 100), SlotKeys.Helmet));
        Assert.Equal("55", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet, armour: 100, evasion: 100), SlotKeys.Helmet));
        Assert.Equal("56", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet, armour: 100, energyShield: 100), SlotKeys.Helmet));
        Assert.Equal("57", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet, evasion: 100, energyShield: 100), SlotKeys.Helmet));
    }

    [Fact]
    public void ResolveBase_maps_weapons_by_item_class()
    {
        Assert.Equal("11", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Claw), SlotKeys.Weapon));
        Assert.Equal("12", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Dagger), SlotKeys.Weapon));
        Assert.Equal("13", AffixPoolTags.ResolveBase(ItemOf(ItemClass.OneHandSword), SlotKeys.Weapon));
        Assert.Equal("22", AffixPoolTags.ResolveBase(ItemOf(ItemClass.TwoHandSword), SlotKeys.Weapon));
        Assert.Equal("15", AffixPoolTags.ResolveBase(ItemOf(ItemClass.OneHandAxe), SlotKeys.Weapon));
        Assert.Equal("24", AffixPoolTags.ResolveBase(ItemOf(ItemClass.TwoHandAxe), SlotKeys.Weapon));
        Assert.Equal("16", AffixPoolTags.ResolveBase(ItemOf(ItemClass.OneHandMace), SlotKeys.Weapon));
        Assert.Equal("23", AffixPoolTags.ResolveBase(ItemOf(ItemClass.TwoHandMace), SlotKeys.Weapon));
        Assert.Equal("17", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Sceptre), SlotKeys.Weapon));
        Assert.Equal("18", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Wand), SlotKeys.Weapon));
        Assert.Equal("216", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Spear), SlotKeys.Weapon));
        Assert.Equal("217", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Flail), SlotKeys.Weapon));
        Assert.Equal("20", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Bow), SlotKeys.Weapon));
        Assert.Equal("21", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Staff), SlotKeys.Weapon));
        Assert.Equal("25", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Warstaff), SlotKeys.Weapon));
        Assert.Equal("228", AffixPoolTags.ResolveBase(ItemOf(ItemClass.Crossbow), SlotKeys.Weapon));
    }

    [Fact]
    public void ResolveBase_returns_null_when_it_cannot_tell()
    {
        // 缺属性（防御值拿不到）→ 判不出来，返回 null，调用方按「池不可用」处理。
        Assert.Null(AffixPoolTags.ResolveBase(ItemOf(ItemClass.Boots), SlotKeys.Boots));
        Assert.Null(AffixPoolTags.ResolveBase(ItemOf(ItemClass.Helmet), SlotKeys.Helmet));

        // 三属性护甲 / 三属性盾：CoE 里没有对应底材，不猜。
        Assert.Null(AffixPoolTags.ResolveBase(
            ItemOf(ItemClass.BodyArmour, armour: 100, evasion: 100, energyShield: 100),
            SlotKeys.BodyArmour));
        Assert.Null(AffixPoolTags.ResolveBase(
            ItemOf(ItemClass.Shield, armour: 100, evasion: 100, energyShield: 100),
            SlotKeys.Offhand));

        // 法器的 dex+int 组合没有底材（法器只有纯 int 那一种）。
        Assert.Null(AffixPoolTags.ResolveBase(ItemOf(ItemClass.Shield, evasion: 100, energyShield: 100), SlotKeys.Offhand));

        // 非常见底材 / 类别与槽位都判不出来。
        Assert.Null(AffixPoolTags.ResolveBase(ItemOf(ItemClass.FishingRod), SlotKeys.Weapon));
        Assert.Null(AffixPoolTags.ResolveBase(ItemOf(ItemClass.Unknown), SlotKeys.Weapon));
        Assert.Null(AffixPoolTags.ResolveBase(ItemOf(ItemClass.Unknown), SlotKeys.Unknown));
        Assert.Null(AffixPoolTags.ResolveBase(null, null));
        Assert.Null(AffixPoolTags.ResolveBase(null, SlotKeys.Weapon));

        // 物品拿不到、但界面槽位是首饰槽时仍然判得出来（兜底路径），这不是「猜」。
        Assert.Equal("3", AffixPoolTags.ResolveBase(null, SlotKeys.Belt));

        // 单手双手分不清的剑/斧/锤（只有 weightKey 那把伞，没有具体类别）→ 不猜。
        var untellableSword = new Item(GameType.Poe2, new OriginalText("Rarity: Rare\nTest Sword"))
        {
            ItemClass = new ItemClassDefinition { Id = "Sword", Type = ItemClass.Unknown },
        };
        Assert.Null(AffixPoolTags.ResolveBase(untellableSword, SlotKeys.Weapon));
    }

    /// <summary>
    /// 界面文案必须说清数据来路：写 Craft of Exile 的做装模型数据，且保留
    ///「这是期望均值、不是保证；官方不公布成功率」—— 两条都不能少，中英都要。
    /// </summary>
    [Fact]
    public void Source_wording_points_at_craft_of_exile_and_keeps_the_caveat()
    {
        var manager = new ResourceManager(
            "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
            typeof(BuildTargetResources).Assembly);

        var zh = manager.GetString("Craft_Prob_Note_Source", CultureInfo.GetCultureInfo("zh"));
        var en = manager.GetString("Craft_Prob_Note_Source", CultureInfo.InvariantCulture);

        Assert.False(string.IsNullOrWhiteSpace(en), "缺英文 Craft_Prob_Note_Source");
        Assert.False(string.IsNullOrWhiteSpace(zh), "缺中文 Craft_Prob_Note_Source");

        // 新旧口径：必须写明数据来源是 Craft of Exile，旧口径（PoB 重组器实验）必须已经不在了。
        Assert.Contains("Craft of Exile", en!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Craft of Exile", zh!, StringComparison.Ordinal);
        Assert.DoesNotContain("Path of Building", en!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path of Building", zh!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("重组器", zh!, StringComparison.Ordinal);

        // 期望均值不是保证 + 官方不公布成功率。
        Assert.Contains("not a promise", en!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not publish", en!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("期望均值", zh!, StringComparison.Ordinal);
        Assert.Contains("官方不公布成功率", zh!, StringComparison.Ordinal);

        // 概率数据找不到时提示的文件名必须是新的那份。
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("zh") })
        {
            Assert.Contains(
                "affix-pool-coe.json",
                manager.GetString("Craft_Prob_No_Weights", culture)!,
                StringComparison.Ordinal);
            Assert.Contains(
                "affix-pool-coe.json",
                manager.GetString("Craft_Prob_Weights_Hint", culture)!,
                StringComparison.Ordinal);
        }
    }

    /// <summary>按物品类别建一件测试装备（防御值用来推导属性组合）。</summary>
    private static Item ItemOf(
        ItemClass itemClass,
        int armour = 0,
        int evasion = 0,
        int energyShield = 0)
    {
        var item = new Item(GameType.Poe2, new OriginalText("Rarity: Rare\nTest Item"))
        {
            ItemClass = new ItemClassDefinition { Id = itemClass.ToString(), Type = itemClass },
        };

        item.Properties.Armour = armour;
        item.Properties.EvasionRating = evasion;
        item.Properties.EnergyShield = energyShield;
        return item;
    }
}
