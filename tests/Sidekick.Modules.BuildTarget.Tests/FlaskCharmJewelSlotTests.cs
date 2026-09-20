using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sidekick.Common;
using Sidekick.Game;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.Languages.Implementations;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Providers;
using Sidekick.Game.TradeStats;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;
using Xunit.Abstractions;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 小事c：药剂 / 咒符 / 珠宝三类槽位的口径核对。
///
/// 口径（按 PoB2 的权威形态定，见 <see cref="SlotKeys"/> 的注释）：
///   · 药剂 **2 槽**（生命 / 魔力）—— 不是「一个药剂槽」；
///   · 咒符 **3 槽**（PoB 的 Charm 1/2/3）；
///   · 珠宝 **1 槽（聚合）**—— 珠宝在 PoB 里是天赋树上的镶嵌孔，没有槽位名。
/// 再加一条：这三类**不做部位过滤**（权重表里没有 flask / charm / jewel 标签），
/// 但概率池照常可用（按 CoE 底材 60/61/241/26-28 取）。
/// </summary>
public class FlaskCharmJewelSlotTests
{
    private readonly ITestOutputHelper output;
    private readonly AffixWeightService weights = new(NullLogger<AffixWeightService>.Instance);
    private readonly DataProvider dataProvider = new(
        Options.Create(new SidekickConfiguration { ApplicationType = SidekickApplicationType.Test }),
        NullLogger<DataProvider>.Instance);
    private readonly AffixPoolStatFilter filter;
    private readonly AffixPoolCoEService CoeService = new(NullLogger<AffixPoolCoEService>.Instance);

    public FlaskCharmJewelSlotTests(ITestOutputHelper output)
    {
        this.output = output;
        filter = new AffixPoolStatFilter(weights, dataProvider, NullLogger<AffixPoolStatFilter>.Instance);
    }

    // ---- 槽位解析 ----

    [Fact]
    public void Life_and_mana_flasks_land_in_two_different_slots()
    {
        // ⚠ 这条就是「2 槽 vs 1 槽」的判据：两件药剂必须落进**不同**的槽键。
        // 若按「各算一个槽」，两者都是 "flask"，导入 BD 时后一件会静默覆盖前一件。
        var life = SlotKeys.ResolveFor(Item(ItemClass.LifeFlask));
        var mana = SlotKeys.ResolveFor(Item(ItemClass.ManaFlask));

        Assert.Equal([SlotKeys.Flask1], life);
        Assert.Equal([SlotKeys.Flask2], mana);
        Assert.NotEqual(life[0], mana[0]);
    }

    [Fact]
    public void Charms_resolve_to_three_slots_and_jewels_to_one()
    {
        Assert.Equal([SlotKeys.Charm1, SlotKeys.Charm2, SlotKeys.Charm3], SlotKeys.ResolveFor(Item(ItemClass.Charms)));
        Assert.Equal([SlotKeys.Jewel], SlotKeys.ResolveFor(Item(ItemClass.Jewel)));
    }

    [Fact]
    public void All_slots_cover_the_six_new_keys_exactly_once()
    {
        string[] expected =
        [
            SlotKeys.Flask1, SlotKeys.Flask2,
            SlotKeys.Charm1, SlotKeys.Charm2, SlotKeys.Charm3,
            SlotKeys.Jewel,
        ];

        foreach (var key in expected)
        {
            Assert.Equal(1, SlotKeys.All.Count(x => x == key));
        }

        // 十个原有部位 + 六个新槽位：数量钉死，改动必须是有意的
        Assert.Equal(16, SlotKeys.All.Length);
    }

    // ---- 部位过滤：这三类不过滤，其它部位照旧过滤 ----

    [Theory]
    [InlineData(ItemClass.LifeFlask, SlotKeys.Flask1)]
    [InlineData(ItemClass.ManaFlask, SlotKeys.Flask2)]
    [InlineData(ItemClass.Charms, SlotKeys.Charm1)]
    [InlineData(ItemClass.Jewel, SlotKeys.Jewel)]
    public void These_categories_opt_out_of_slot_filtering(ItemClass type, string slotKey)
    {
        var tags = AffixPoolTags.Resolve(Item(type), slotKey);

        // 标签集是空的（权重表没有这三个部位的标签）—— 但**必须**带 NoSlotFilter，
        // 界面才能把原因说成「这类别不做部位过滤」，而不是「认不出这件装备」。
        Assert.True(tags.NoSlotFilter);
        Assert.True(tags.IsEmpty);
        Assert.Equal(AffixPoolTags.NoSlotFilterReason, tags.Reason);
    }

    [Fact]
    public async Task The_three_categories_list_the_whole_library_while_a_helmet_still_filters()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        // 头盔：照旧严格过滤（这条保证 NoSlotFilter 不是「对谁都为真」的空断言）
        var helmet = AffixPoolTags.Resolve(Item(ItemClass.Helmet, armour: 120), SlotKeys.Helmet);
        Assert.False(helmet.NoSlotFilter);
        Assert.Equal(4, AffixSearchFilter.Apply(matched, index, helmet.Tags).Count);

        // 药剂 / 咒符 / 珠宝：一条都不能被过滤掉（否则界面看着像功能坏了）
        foreach (var (type, slotKey) in new[]
                 {
                     (ItemClass.LifeFlask, SlotKeys.Flask1),
                     (ItemClass.ManaFlask, SlotKeys.Flask2),
                     (ItemClass.Charms, SlotKeys.Charm1),
                     (ItemClass.Jewel, SlotKeys.Jewel),
                 })
        {
            var tags = AffixPoolTags.Resolve(Item(type), slotKey);
            var hits = AffixSearchFilter.Apply(matched, index, tags.Tags);

            output.WriteLine($"{slotKey}：未过滤 {matched.Count} 条，过滤后 {hits.Count} 条");
            Assert.Equal(matched.Count, hits.Count);
        }
    }

    // ---- 概率池：这三类的 CoE 底材 id ----

    [Fact]
    public void Known_bases_resolve_for_flasks_charms_and_jewels()
    {
        Assert.Equal("60", AffixPoolTags.ResolveBase(Item(ItemClass.LifeFlask), SlotKeys.Flask1));
        Assert.Equal("61", AffixPoolTags.ResolveBase(Item(ItemClass.ManaFlask), SlotKeys.Flask2));
        Assert.Equal("241", AffixPoolTags.ResolveBase(Item(ItemClass.Charms), SlotKeys.Charm1));

        // 珠宝按**基底名**取（类别下混着 9 个底子）
        Assert.Equal("26", AffixPoolTags.ResolveBase(Jewel("紅寶石"), SlotKeys.Jewel));
        Assert.Equal("27", AffixPoolTags.ResolveBase(Jewel("綠寶石"), SlotKeys.Jewel));
        Assert.Equal("28", AffixPoolTags.ResolveBase(Jewel("藍寶石"), SlotKeys.Jewel));
        Assert.Equal("26", AffixPoolTags.ResolveBase(Jewel("Ruby"), SlotKeys.Jewel));
    }

    [Fact]
    public void Uncovered_jewel_bases_stay_null_instead_of_guessing()
    {
        // 時迭 / 永恆 / 鑽石 在 CoE 里没有对应底材 → 返回 null（池不可用，如实说）。
        // ⚠ 「時迭藍寶石」不许退到一般藍寶石（28）上 —— 那是猜，会把错池的权重报给用户。
        foreach (var name in new[] { "時迭藍寶石", "時迭鑽石", "永恆珠寶", "鑽石", "Time-Lost Sapphire" })
        {
            Assert.Null(AffixPoolTags.ResolveBase(Jewel(name), SlotKeys.Jewel));
        }
    }

    [Fact]
    public void These_pools_are_real_in_the_coe_dataset()
    {
        var coe = CoeService;

        foreach (var baseId in new[] { "60", "61", "241", "26", "27", "28" })
        {
            Assert.True(coe.Data!.Bases.ContainsKey(baseId), $"CoE 数据集里没有底材 {baseId}");

            var suffix = coe.GetPool(baseId, AffixSide.Suffix, itemLevel: 82);
            var prefix = coe.GetPool(baseId, AffixSide.Prefix, itemLevel: 82);

            output.WriteLine($"底材 {baseId}（{coe.Data.Bases[baseId].Name}）：前缀 {prefix.Count} 档 / 后缀 {suffix.Count} 档");

            // 池非空 = 期望成本算得出来（不会退回「认不出底材」）
            Assert.NotEmpty(suffix);
            Assert.NotEmpty(prefix);
        }
    }

    // ---- 试穿：PoB 槽名映射 ----

    [Fact]
    public void Flask_and_charm_slot_keys_map_to_the_pob_slot_names()
    {
        Assert.Equal("Flask 1", PobCompareService.MapSlot(SlotKeys.Flask1));
        Assert.Equal("Flask 2", PobCompareService.MapSlot(SlotKeys.Flask2));
        Assert.Equal("Charm 1", PobCompareService.MapSlot(SlotKeys.Charm1));
        Assert.Equal("Charm 2", PobCompareService.MapSlot(SlotKeys.Charm2));
        Assert.Equal("Charm 3", PobCompareService.MapSlot(SlotKeys.Charm3));

        // 珠宝没有槽位名 —— 引擎试穿走 SocketedItem 那条专门状态，不能映射成某个孔
        Assert.Null(PobCompareService.MapSlot(SlotKeys.Jewel));
    }

    [Fact]
    public void CoE_names_of_the_new_bases_match_the_dataset()
    {
        var coe = CoeService;

        Assert.Equal("Life Flask", coe.Data!.Bases["60"].Name);
        Assert.Equal("Mana Flask", coe.Data.Bases["61"].Name);
        Assert.Equal("Charm", coe.Data.Bases["241"].Name);
        Assert.Equal("Ruby", coe.Data.Bases["26"].Name);
        Assert.Equal("Emerald", coe.Data.Bases["27"].Name);
        Assert.Equal("Sapphire", coe.Data.Bases["28"].Name);
    }

    // ---- 文案 ----

    [Fact]
    public void New_strings_exist_in_both_languages()
    {
        var manager = new System.Resources.ResourceManager(
            "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
            typeof(Sidekick.Modules.BuildTarget.Localization.BuildTargetResources).Assembly);

        string[] keys =
        [
            "Slot_flask1", "Slot_flask2", "Slot_charm1", "Slot_charm2", "Slot_charm3", "Slot_jewel",
            "Search_Filter_NoSlotTags",
            "Pob_Socketed_Item",
        ];

        foreach (var key in keys)
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture);
            var chinese = manager.GetString(key, CultureInfo.GetCultureInfo("zh"));

            Assert.False(string.IsNullOrEmpty(english), $"{key} 缺少英文文案");
            Assert.False(string.IsNullOrEmpty(chinese), $"{key} 缺少中文文案");
        }
    }

    // ---- 脚手架 ----

    private async Task<AffixPoolIndex> IndexAsync()
    {
        Assert.True(
            weights.HasData,
            $"找不到 affix-weights.json（查找路径：{string.Join(" | ", weights.SearchedPaths)}）");

        var index = await filter.GetIndexAsync(GameType.Poe2);
        Assert.NotNull(index);

        return index!;
    }

    /// <summary>中文词缀库里搜「抗性」（与 StatPoolFilterTests 同一把尺子，便于对照）。</summary>
    private async Task<List<TradeStatDefinition>> SearchAsync()
    {
        var stats = await dataProvider.Read<List<TradeStatDefinition>>(
            GameType.Poe2,
            GameDataType.TradeStats,
            new GameLanguageZh());

        return
        [
            .. stats
                .GroupBy(x => x.Text)
                .Select(x => x.First())
                .Where(x => x.Text.Contains("抗性", StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static Item Item(ItemClass type, int armour = 0, int evasion = 0, int energyShield = 0)
    {
        var item = new Item(GameType.Poe2, new OriginalText($"Rarity: Rare\nTest {type}"))
        {
            ItemClass = new ItemClassDefinition { Id = type.ToString(), Type = type },
        };

        item.Properties.Armour = armour;
        item.Properties.EvasionRating = evasion;
        item.Properties.EnergyShield = energyShield;

        return item;
    }

    /// <summary>珠宝：底材名在 <see cref="Item.Type"/> 上（如「藍寶石」/「Sapphire」）。</summary>
    private static Item Jewel(string baseName)
    {
        var item = Item(ItemClass.Jewel);
        item.Type = baseName;
        return item;
    }
}
