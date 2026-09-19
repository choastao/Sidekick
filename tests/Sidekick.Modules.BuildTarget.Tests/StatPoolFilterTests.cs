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
/// 词缀搜索器「只显示本部位能出的词缀」的核对。
///
/// 词缀库（当前语言）和权重表（英文）是两套文本，靠**跨语言相同的统计 id** 对上，
/// 所以这里走真实数据：中文词缀库 + 英文词缀库 + affix-weights.json。
/// 期望条数是按同一套规则用脚本数出来的，逐条核对必须完全一致。
/// </summary>
public class StatPoolFilterTests
{
    /// <summary>用户实测用的关键词（中文客户端搜「抗性」）。</summary>
    private const string Keyword = "抗性";

    private readonly ITestOutputHelper output;
    private readonly AffixWeightService weights = new(NullLogger<AffixWeightService>.Instance);
    private readonly DataProvider dataProvider = new(
        Options.Create(new SidekickConfiguration { ApplicationType = SidekickApplicationType.Test }),
        NullLogger<DataProvider>.Instance);
    private readonly AffixPoolStatFilter filter;

    public StatPoolFilterTests(ITestOutputHelper output)
    {
        this.output = output;
        filter = new AffixPoolStatFilter(weights, dataProvider, NullLogger<AffixPoolStatFilter>.Instance);
    }

    [Fact]
    public async Task Helmet_search_reports_filtered_and_unfiltered_counts()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        var helmet = AffixPoolTags.Resolve(Item(ItemClass.Helmet, armour: 120), SlotKeys.Helmet);
        Assert.Contains(AffixPoolTags.Armour, helmet.Tags);

        var filtered = AffixSearchFilter.Apply(matched, index, helmet.Tags);
        output.WriteLine($"头盔：未过滤 {matched.Count} 条，过滤后 {filtered.Count} 条 [{helmet.TagText}]");

        // 未过滤 113 条中文词缀里，头盔真正能出的只有四条基础抗性
        Assert.Equal(113, matched.Count);
        Assert.Equal(4, filtered.Count);

        // 四条基础抗性必须还在，别把该留的过滤掉（文本是繁体，因为跟随物品语言）
        foreach (var resistance in new[] { "火焰抗性", "冰冷抗性", "閃電抗性", "混沌抗性" })
        {
            Assert.Contains(filtered, x => x.Text.Contains(resistance));
        }
    }

    [Fact]
    public async Task Helmet_without_defence_values_still_uses_the_armour_umbrella()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        // 防御值读不到 → AffixPoolTags 走退化路径：{helmet, armour}，并且如实标 Degraded
        var degraded = AffixPoolTags.Resolve(Item(ItemClass.Helmet), SlotKeys.Helmet);
        Assert.True(degraded.Degraded);
        Assert.Equal(["helmet", "armour"], degraded.Tags);

        var filtered = AffixSearchFilter.Apply(matched, index, degraded.Tags);
        output.WriteLine($"头盔（读不到防御值）：未过滤 {matched.Count} 条，过滤后 {filtered.Count} 条 [{degraded.TagText}]");

        // 抗性挂的就是 armour 这把伞，子类拿不到也不影响它留在池里
        Assert.Equal(4, filtered.Count);
    }

    [Fact]
    public async Task Boots_search_keeps_the_same_armour_pool_as_the_helmet()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        // 鞋子能出抗性靠的是 armour 这把伞，不是 boots 键
        var boots = AffixPoolTags.Resolve(Item(ItemClass.Boots, evasion: 150), SlotKeys.Boots);
        Assert.Contains(AffixPoolTags.Armour, boots.Tags);
        Assert.Contains("dex_armour", boots.Tags);

        var filtered = AffixSearchFilter.Apply(matched, index, boots.Tags);
        output.WriteLine($"鞋子（dex_armour）：未过滤 {matched.Count} 条，过滤后 {filtered.Count} 条 [{boots.TagText}]");

        Assert.Equal(113, matched.Count);
        Assert.Equal(4, filtered.Count);
        Assert.Contains(filtered, x => x.Text.Contains("火焰抗性"));
    }

    [Fact]
    public async Task Ring_search_differs_from_the_helmet()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        var helmet = AffixPoolTags.Resolve(Item(ItemClass.Helmet, armour: 120), SlotKeys.Helmet);
        var ring = AffixPoolTags.Resolve(Item(ItemClass.Ring), SlotKeys.Ring1);

        // 戒指没有 armour 伞：只有 {ring}
        Assert.Equal([AffixPoolTags.Ring], ring.Tags);
        Assert.DoesNotContain(AffixPoolTags.Armour, ring.Tags);

        var helmetHits = AffixSearchFilter.Apply(matched, index, helmet.Tags);
        var ringHits = AffixSearchFilter.Apply(matched, index, ring.Tags);
        output.WriteLine($"戒指：未过滤 {matched.Count} 条，过滤后 {ringHits.Count} 条 [{ring.TagText}]");

        Assert.Equal(113, matched.Count);
        Assert.Equal(5, ringHits.Count);

        // 戒指比头盔多一条：「全部元素抗性」戒指能出、头盔出不了
        Assert.NotEqual(helmetHits.Count, ringHits.Count);
        Assert.Contains(ringHits, x => x.Text.Contains("全元素抗性"));
        Assert.DoesNotContain(helmetHits, x => x.Text.Contains("全元素抗性"));
    }

    /// <summary>
    /// 审计（2026-09-19）抓到的回归：盾牌专用词缀挂的是 str_shield / str_dex_shield / str_int_shield，
    /// 而 DefenceSubtype 产出的是 *_armour 那一族 —— 两边对不上时盾牌能出的词缀会被**静默**藏掉
    /// （Degraded=false，界面不给任何提示），同一个标签集进成本池还会让期望成本偏低。
    /// </summary>
    [Fact]
    public async Task Shield_can_see_its_own_affix_family()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        var shield = AffixPoolTags.Resolve(Item(ItemClass.Shield, armour: 200), "shield");

        // 两族都要在：str_shield 是数据里真实用的键（AdditionalPhysicalDamageReduction1-5_
        // 的权重就是 {default:1, str_shield:1}，default 恒 0 = 排除），str_armour 留着防止误藏。
        Assert.Contains(AffixPoolTags.Armour, shield.Tags);
        Assert.Contains("str_shield", shield.Tags);
        Assert.Contains("str_armour", shield.Tags);

        var filtered = AffixSearchFilter.Apply(matched, index, shield.Tags);
        output.WriteLine($"力量盾：未过滤 {matched.Count} 条，过滤后 {filtered.Count} 条 [{shield.TagText}]");

        // ⚠ 这里原来断言 filtered 含「全元素抗性」—— 是假绿，已删（复审 2026-09-19 指出）：
        //   MaximumElementalResistance1/2 的权重就是 {"shield":1}，而 "shield" 是本槽位键、
        //   永远在标签集里，所以那条断言对任何盾牌都恒真，根本测不到 str_shield 这一族。
        //   而真正含「全元素抗性」的 AllResistances1-6 权重是 {str_int_shield, ring, amulet}，
        //   **力量盾本来就看不到** —— 原来那条注释把它按在力量盾上是张冠李戴。
        //   单属性 / 混合盾的家族键覆盖由 StrInt_shield_... 用例和 AffixPoolChanceTests 保证。
        Assert.NotEmpty(filtered);
    }

    [Fact]
    public async Task StrInt_shield_uses_str_int_shield_key()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        // 力智盾：AllResistances1-6 的权重是 {str_int_shield:1, ring:1, amulet:1} ——
        // 盾牌键只有 str_int_shield 这一个
        var shield = AffixPoolTags.Resolve(Item(ItemClass.Shield, armour: 100, energyShield: 50), "shield");

        Assert.Contains("str_int_shield", shield.Tags);
        Assert.DoesNotContain("str_shield", shield.Tags);

        var filtered = AffixSearchFilter.Apply(matched, index, shield.Tags);
        output.WriteLine($"力智盾：未过滤 {matched.Count} 条，过滤后 {filtered.Count} 条 [{shield.TagText}]");

        // 上面那句「含全元素抗性」单独用是假绿（MaximumElementalResistance 的 w 就是 {"shield":1}，
        // 任何盾牌都满足）—— 必须排除掉它，剩下的才是 AllResistances 这一族。
        // 对照：Helmet_... 用例断言过头盔看不到任何含「全元素抗性」的条目，两边合起来才构成有效对照。
        Assert.Contains(filtered, x => x.Text.Contains("全元素抗性") && !x.Text.Contains("最大"));
    }

    /// <summary>纯敏盾在数据里没有对应的 shield 键，不许硬造 —— 只保留 armour 伞和 *_armour。</summary>
    [Fact]
    public async Task Dex_shield_does_not_invent_a_missing_key()
    {
        var shield = AffixPoolTags.Resolve(Item(ItemClass.Shield, evasion: 180), "shield");

        Assert.Contains("dex_armour", shield.Tags);
        Assert.DoesNotContain("dex_shield", shield.Tags);
        Assert.DoesNotContain("str_shield", shield.Tags);
    }

    [Fact]
    public async Task Search_without_item_context_is_not_filtered()
    {
        var index = await IndexAsync();
        var matched = await SearchAsync();

        // 打开搜索器时没有正在看的装备：组件根本不会去建标签集，索引也不给 —— 原样返回
        var withoutContext = AffixSearchFilter.Apply(matched, index: null, tags: null);

        // 兜底：标签集是空的也原样返回，绝不能按空池把整库过滤掉
        var withoutTags = AffixSearchFilter.Apply(matched, index, tags: []);

        output.WriteLine($"无上下文：匹配 {matched.Count} 条，返回 {withoutContext.Count} / {withoutTags.Count} 条");

        Assert.Equal(matched.Count, withoutContext.Count);
        Assert.Equal(matched.Count, withoutTags.Count);
        Assert.Equal(matched.Select(x => x.Text), withoutContext.Select(x => x.Text));
    }

    [Fact]
    public async Task English_library_filters_exactly_the_same_way()
    {
        var index = await IndexAsync();

        var stats = await dataProvider.Read<List<TradeStatDefinition>>(
            GameType.Poe2,
            GameDataType.TradeStats,
            new GameLanguageEn());

        var matched = stats
            .GroupBy(x => x.Text)
            .Select(x => x.First())
            .Where(x => x.Text.Contains("Resistance", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var helmet = AffixPoolTags.Resolve(Item(ItemClass.Helmet, armour: 120), SlotKeys.Helmet);
        var ring = AffixPoolTags.Resolve(Item(ItemClass.Ring), SlotKeys.Ring1);

        var helmetHits = AffixSearchFilter.Apply(matched, index, helmet.Tags);
        var ringHits = AffixSearchFilter.Apply(matched, index, ring.Tags);
        output.WriteLine($"英文词缀库 Resistance：未过滤 {matched.Count} 条，头盔 {helmetHits.Count} 条，戒指 {ringHits.Count} 条");

        // 英文客户端走的是同一条路（id 直查 + 归一化文本），结果应当和中文字典一致
        Assert.Equal(118, matched.Count);
        Assert.Equal(4, helmetHits.Count);
        Assert.Equal(5, ringHits.Count);
    }

    [Fact]
    public async Task Index_translates_the_current_language_library_through_english_ids()
    {
        var index = await IndexAsync();

        // 中文词缀「#%火焰抗性」的英文原文是「#% to Fire Resistance」，
        // 靠统计 id 翻译过去才可能在权重表里找到 FireResist 那几条
        Assert.True(index.StatIdCount > 0);

        var fire = (await LibraryAsync()).First(x => x.Text == "#%火焰抗性");
        Assert.True(index.IsListed(fire.Id, fire.Text));

        // 独一无二的装备词缀（权重表里没有）不该被当成能出的词缀
        foreach (var text in new[] { "# 元素抗性", "混沌抗性為0", "閃電抗性不影響承受的閃電傷害" })
        {
            var stat = (await LibraryAsync()).First(x => x.Text == text);
            Assert.False(index.IsListed(stat.Id, stat.Text));
        }
    }

    [Fact]
    public void Filter_strings_exist_in_both_languages_with_matching_placeholders()
    {
        var manager = new System.Resources.ResourceManager(
            "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
            typeof(Sidekick.Modules.BuildTarget.Localization.BuildTargetResources).Assembly);

        string[] keys =
        [
            "Search_Filter_BySlot",
            "Search_Filter_Count",
            "Search_Filter_Total",
            "Search_Filter_NoItem",
            "Search_Filter_NoSlot",
            "Search_Filter_Unavailable",
            "Search_Filter_Tags",
            "Search_Filter_Degraded",
        ];

        foreach (var key in keys)
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture);
            var chinese = manager.GetString(key, CultureInfo.GetCultureInfo("zh"));

            Assert.False(string.IsNullOrEmpty(english), $"{key} 缺少英文文案");
            Assert.False(string.IsNullOrEmpty(chinese), $"{key} 缺少中文文案");
            Assert.Equal(Signature(english!), Signature(chinese!));

            // 界面上不许出现旧名字
            Assert.DoesNotContain("Sidekick", english!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sidekick", chinese!, StringComparison.OrdinalIgnoreCase);

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
        var matches = System.Text.RegularExpressions.Regex.Matches(format, @"\{(\d+)");
        return matches.Count == 0
                   ? 0
                   : matches.Max(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
    }

    private async Task<AffixPoolIndex> IndexAsync()
    {
        Assert.True(
            weights.HasData,
            $"找不到 affix-weights.json（查找路径：{string.Join(" | ", weights.SearchedPaths)}）");

        var index = await filter.GetIndexAsync(GameType.Poe2);
        Assert.NotNull(index);

        return index!;
    }

    /// <summary>词缀库里去重排序后的全量列表（和 StatPicker.BuildList 的做法一致）。</summary>
    private async Task<List<TradeStatDefinition>> LibraryAsync()
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
                .OrderBy(x => x.Text),
        ];
    }

    /// <summary>在词缀库里搜关键词（和 StatPicker 一样按原文包含匹配）。</summary>
    private async Task<List<TradeStatDefinition>> SearchAsync()
    {
        var library = await LibraryAsync();
        return [.. library.Where(x => x.Text.Contains(Keyword, StringComparison.OrdinalIgnoreCase))];
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
}
