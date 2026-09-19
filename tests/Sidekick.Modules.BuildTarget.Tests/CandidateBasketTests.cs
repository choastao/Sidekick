using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidekick.Game;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Parser.Stats;
using Sidekick.Game.Stats;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 备选篮的核对：物品是用构造的 <see cref="Item"/>（不走文件、不跑解析器），
/// 词缀只给「id + 数值」，正好验证算法认的是词缀 id 而不是物品原文。
///
/// 用到的 id 都是从 data/{poe1,poe2}/{lang}/stats.json 里查出来的真实编号
/// （同一编号在两种客户端语言、两个游戏里都指同一条词缀）。
/// </summary>
public class CandidateBasketTests
{
    // ---- 验收 a：+25% 火焰抗性 + +15% 全部元素抗性 → 火 40 / 冰 15 / 电 15 / 混 0 ----

    [Fact]
    public void All_elemental_resistance_counts_once_for_fire_cold_and_lightning()
    {
        var item = MakeItem(
            "Doom Fang",
            "Iron Ring",
            ItemClass.Ring,
            ResistanceStat("+25% to Fire Resistance", 25, "explicit.stat_3372524247"),
            ResistanceStat("+15% to all Elemental Resistances", 15, "explicit.stat_2901986750"));

        var contribution = CandidateBasketCalculator.Contribute(item);

        Assert.Equal(40d, contribution.FireResistance);
        Assert.Equal(15d, contribution.ColdResistance);
        Assert.Equal(15d, contribution.LightningResistance);
        Assert.Equal(0d, contribution.ChaosResistance);

        // 属性不该被抗性词缀带出来
        Assert.Equal(0d, contribution.Strength);
        Assert.Equal(0d, contribution.Dexterity);
        Assert.Equal(0d, contribution.Intelligence);
    }

    // ---- 验收 b：+20 力量 + +10 全部能力值 → 力 30 / 敏 10 / 智 10 ----

    [Fact]
    public void All_attributes_counts_once_for_strength_dexterity_and_intelligence()
    {
        var item = MakeItem(
            "Doom Fang",
            "Iron Ring",
            ItemClass.Ring,
            ResistanceStat("+20 to Strength", 20, "explicit.stat_4080418644"),
            ResistanceStat("+10 to all Attributes", 10, "explicit.stat_1379411836"));

        var contribution = CandidateBasketCalculator.Contribute(item);

        Assert.Equal(30d, contribution.Strength);
        Assert.Equal(10d, contribution.Dexterity);
        Assert.Equal(10d, contribution.Intelligence);
        Assert.Equal(0d, contribution.FireResistance);
    }

    // ---- 验收 c：勾掉一件后被正确排除在合计外 ----

    [Fact]
    public void Unchecked_candidates_are_left_out_of_the_total()
    {
        using var scope = new BasketScope();
        var basket = scope.Service;

        var helmet = basket.Add(
            MakeItem("Doom Fang", "Iron Helmet", ItemClass.Helmet, ResistanceStat("+25% to Fire Resistance", 25, "explicit.stat_3372524247")),
            SlotKeys.Helmet);

        var ring = basket.Add(
            MakeItem("Gloom Lash", "Iron Ring", ItemClass.Ring, ResistanceStat("+15% to Fire Resistance", 15, "explicit.stat_3372524247")),
            SlotKeys.Ring1);

        Assert.Equal(2, basket.Items.Count);
        Assert.Equal(40d, CandidateBasketCalculator.Sum(basket.Items).FireResistance);

        // 勾掉头盔 → 只算戒指
        basket.SetEnabled(helmet.Id, false);
        Assert.Equal(15d, CandidateBasketCalculator.Sum(basket.Items).FireResistance);

        // 再点一次（开关语义）→ 回到 40
        basket.Toggle(helmet.Id);
        Assert.True(helmet.Enabled);
        Assert.Equal(40d, CandidateBasketCalculator.Sum(basket.Items).FireResistance);

        // 移除戒指 → 只剩头盔
        basket.Remove(ring.Id);
        Assert.Single(basket.Items);
        Assert.Equal(25d, CandidateBasketCalculator.Sum(basket.Items).FireResistance);

        basket.Clear();
        Assert.Empty(basket.Items);
        Assert.Equal(0d, CandidateBasketCalculator.Sum(basket.Items).FireResistance);
    }

    // ---- 验收 d：同一件重复加入不重复计 ----

    [Fact]
    public void Adding_the_same_item_twice_overwrites_instead_of_double_counting()
    {
        using var scope = new BasketScope();
        var basket = scope.Service;

        var first = basket.Add(
            MakeItem("Doom Fang", "Iron Ring", ItemClass.Ring, ResistanceStat("+25% to Fire Resistance", 25, "explicit.stat_3372524247")),
            SlotKeys.Ring1);

        // 同一件装备（物品文本相同）从别的地方再复制一次加进来
        var second = basket.Add(
            MakeItem("Doom Fang", "Iron Ring", ItemClass.Ring, ResistanceStat("+25% to Fire Resistance", 25, "explicit.stat_3372524247")),
            SlotKeys.Ring1);

        Assert.Single(basket.Items);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(25d, CandidateBasketCalculator.Sum(basket.Items).FireResistance);
        Assert.Equal(1, basket.Items.Count(x => x.Enabled));
    }

    // ---- 附加：默认上限、落盘读回、双语资源 ----

    [Fact]
    public void Resistance_cap_defaults_to_75_and_is_persisted()
    {
        var path = TempPath();
        var basket = NewBasket(path);
        Assert.Equal(75d, basket.ResistanceCap);

        basket.ResistanceCap = 78;
        basket.Add(
            MakeItem("Doom Fang", "Iron Ring", ItemClass.Ring, ResistanceStat("+25% to Fire Resistance", 25, "explicit.stat_3372524247")),
            SlotKeys.Ring1);

        // 重新读盘：备选和上限都还在（内存态 + 可持久化）
        var reloaded = NewBasket(path);
        Assert.Equal(78d, reloaded.ResistanceCap);
        Assert.Single(reloaded.Items);
        Assert.Equal(SlotKeys.Ring1, reloaded.Items[0].SlotKey);
        Assert.Equal(25d, CandidateBasketCalculator.Sum(reloaded.Items).FireResistance);

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 测试临时文件删不掉不影响结论
        }
    }

    /// <summary>
    /// 面板上的文案全部走 string.Format / 资源键：中英两份缺任何一条、占位符对不上，
    /// 都会在切语言时炸在界面上，所以在这里先卡住。
    /// </summary>
    [Fact]
    public void Basket_strings_exist_in_both_languages_with_matching_placeholders()
    {
        var manager = new ResourceManager(
            "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
            typeof(BuildTargetResources).Assembly);

        string[] keys =
        [
            "Tab_Basket",
            "Basket_Empty",
            "Basket_Col_Name",
            "Basket_Col_Slot",
            "Basket_Total",
            "Basket_Enabled_Count",
            "Basket_Clear",
            "Basket_Slot_Unknown",
            "Basket_Cap",
            "Basket_Cap_Ok",
            "Basket_Cap_Short",
            "Basket_Cap_Note",
            "Basket_Attr_Strength",
            "Basket_Attr_Dexterity",
            "Basket_Attr_Intelligence",
            "Basket_Attribute_Note",
            "Basket_Stat_FireResistance",
            "Basket_Stat_ColdResistance",
            "Basket_Stat_LightningResistance",
            "Basket_Stat_ChaosResistance",
            "Basket_Stat_Strength",
            "Basket_Stat_Dexterity",
            "Basket_Stat_Intelligence",
            "Basket_Compare_Title",
            "Basket_Compare_Slot",
            "Basket_Compare_Unparsable",
            "Basket_No_Snapshot",
            "Basket_Note_Scope",
            "Basket_Note_AllEle",
            "Basket_Note_Additive",
        ];

        foreach (var key in keys)
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture);
            var chinese = manager.GetString(key, CultureInfo.GetCultureInfo("zh"));

            Assert.False(string.IsNullOrEmpty(english), $"{key} 缺少英文文案");
            Assert.False(string.IsNullOrEmpty(chinese), $"{key} 缺少中文文案");
            Assert.Equal(Signature(english!), Signature(chinese!));
        }
    }

    // ---- 构造物品的小工具 ----

    /// <summary>造一条「词缀 id + 数值」的词缀行，等价于解析器解析出来的结果。</summary>
    private static Stat ResistanceStat(string text, double value, string tradeId) =>
        new(StatCategory.Explicit, text)
        {
            Definitions =
            [
                new StatDefinition
                {
                    Text = text,
                    TradeIds = [tradeId],
                    Value = value,
                },
            ],
            Values = [value],
        };

    private static Item MakeItem(string name, string baseType, ItemClass itemClass, params Stat[] stats)
    {
        var item = new Item(GameType.Poe2, new OriginalText($"Item Class: Test\nRarity: Rare\n{name}\n{baseType}"))
        {
            Name = name,
            Type = baseType,
            ItemClass = new ItemClassDefinition { Id = "Test", Type = itemClass },
        };

        item.Stats.AddRange(stats);
        return item;
    }

    private static CandidateBasketService NewBasket(string path) =>
        new TempFileBasketService(NullLogger<CandidateBasketService>.Instance, path);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "sidekick-basket-tests", Guid.NewGuid().ToString("N") + ".json");

    /// <summary>测试用的备选篮：落盘到临时文件，绝不碰用户真实的 candidate-basket.json。</summary>
    private sealed class BasketScope : IDisposable
    {
        private readonly string path = TempPath();

        public BasketScope()
        {
            Service = NewBasket(path);
        }

        public CandidateBasketService Service { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    /// <summary>落盘位置换成临时文件的备选篮（真实运行时是 %AppData%\sidekick\candidate-basket.json）。</summary>
    private sealed class TempFileBasketService(ILogger<CandidateBasketService> logger, string path)
        : CandidateBasketService(logger, path);

    /// <summary>文案里用到的参数个数（最大占位符下标 + 1）。</summary>
    private static int Signature(string format)
    {
        var matches = Regex.Matches(format, @"\{(\d+)");
        return matches.Count == 0 ? 0 : matches.Max(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
    }
}
