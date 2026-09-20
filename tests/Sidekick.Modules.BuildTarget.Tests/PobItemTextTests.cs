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
    /// <summary>
    /// 英文模板表 —— ⚠ 这里刻意用**真实形状**：`trade-stats.json` 的模板**不带行首 `+`**
    /// （`+` 在定义正则里、是捕获组外的字面量，见 data/poe2/en/trade-stats.json 的 `# to maximum Life`）。
    /// 之前这里的夹具是手工写成 `+#…` 的，于是「转换输出丢了 `+`」这条真机上炸出来的问题，
    /// 单测永远看不见（审计 C5：给的是假信心）。别改回带 `+` 的写法。
    /// </summary>
    private static readonly Dictionary<string, string> InvariantStats = new()
    {
        ["explicit.stat_fire_res"] = "#% to Fire Resistance",
        ["explicit.stat_life"] = "# to maximum Life",
        ["implicit.stat_es"] = "# to maximum Energy Shield",
        ["explicit.stat_armour_pct"] = "#% increased Armour (Local)",
        // 同族的干净模板：真实数据里 `increased Attack Speed` 就有这一对（crafted.stat_210067635 带注解、
        // crafted.stat_681332047 干净），而 zh 数据里**第一个**可查到的是带注解的那条。
        ["crafted.stat_210067635"] = "#% increased Attack Speed (Local)",
        ["crafted.stat_681332047"] = "#% increased Attack Speed",
        // 格挡只有注解版（真实数据里 5 条候选全是 `(Local)`）→ 走「剥掉注解」的兜底分支
        ["explicit.stat_2481353198"] = "#% increased Block chance (Local)",
        ["explicit.stat_3484657501"] = "# to Armour (Local)",
        // **多行模板**：真实数据里 en trade-stats 有 105 条内嵌 `\n`（zh 侧 127 条定义的**首命中**就是这种），
        // 例：`Burning Enemies you kill have a #% chance to Explode, dealing a\ntenth of their maximum Life as Fire Damage`。
        // 它在 `lines` 里占 1 个下标、在文本里占 2 个物理行 —— 行号记法错了会让后续词缀全部定位错位（审计 C2 阻断 A）。
        ["explicit.stat_multiline"] = "Burning Enemies you kill have a #% chance to Explode, dealing a\ntenth of their maximum Life as Fire Damage",
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
        return Stat(category, text, values, tradeId == null ? null : [tradeId], definitionText);
    }

    private static Stat Stat(StatCategory category, string text, double[] values, string[]? tradeIds, string definitionText)
    {
        return new Stat(category, text)
        {
            Definitions =
            [
                new StatDefinition
                {
                    Text = definitionText,
                    TradeIds = tradeIds?.ToList(),
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

    /// <summary>
    /// 平添型词缀必须补上行首 `+`。实测（tao-plus-sign-test.py）：PoB 对缺 `+` 的行是**静默忽略**的 ——
    /// 同一件头盔追加 `60 to maximum Life` 与不追加，DPS/EHP 逐位相同；带 `+` 才会生效。
    /// 而英文 trade-stats 的模板本来就不带 `+`，所以这一步只能由「命中的定义文本是否以 `+` 开头」决定。
    /// </summary>
    [Fact]
    public void Flat_affixes_get_the_leading_plus_the_template_lacks()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));
        item.Stats.Add(Stat(StatCategory.Implicit, "+30 最大能量护盾", [30], "implicit.stat_es", "+# 最大能量护盾"));

        var result = PobItemText.Build(item, InvariantStats);
        var lines = result.Text.Split('\n').Select(x => x.Trim()).ToList();

        Assert.Equal(2, result.MappedStats);
        Assert.Contains("+88 to maximum Life", lines);
        Assert.Contains("+30 to maximum Energy Shield", lines);

        // 反证：模板里本身没有 `+`，这两行能带 `+` 只能是因为补号那一步生效了
        Assert.DoesNotContain(lines, x => x == "88 to maximum Life");
        Assert.DoesNotContain(lines, x => x == "30 to maximum Energy Shield");
    }

    /// <summary>
    /// 反证的反面：非平添型词缀（定义文本不以 `+` 开头）**不许**被补 `+` ——
    /// 无脑补号会把 `60% increased Armour` 变成 `+60% increased Armour`，同样是错行。
    /// </summary>
    [Fact]
    public void Non_flat_affixes_do_not_get_a_plus()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "60% 增加护甲", [60], "explicit.stat_armour_pct", "#% 增加护甲"));

        var lines = PobItemText.Build(item, InvariantStats).Text.Split('\n').Select(x => x.Trim()).ToList();

        Assert.Contains("60% increased Armour", lines);
        Assert.DoesNotContain(lines, x => x.StartsWith('+') && x.Contains("increased Armour"));
    }

    /// <summary>
    /// **尾部注解不许出现在送进引擎的文本里 —— 并且优先取同族里那条干净的模板。**
    ///
    /// 实测（`pob2-engine/tao-annotation2-test.py`）：带 `(Local)` 的行喂给 PoB = **整行消失**
    /// （`+60 to maximum Life (Local)` 与基线逐位相同，而干净的 `+60 to maximum Life` 让 EHP 涨 256）。
    /// 而真实数据里「增加攻击速度」的第一个可查到模板恰好是带注解的那条 →
    /// 不处理就是又一次「Skipped = 0 但数字算少一块」的假成功，这次还发生在**攻速**上。
    /// </summary>
    [Fact]
    public void Annotated_template_is_skipped_when_a_clean_one_exists()
    {
        var item = Helmet();
        item.Stats.Add(Stat(
            StatCategory.Explicit,
            "30% 增加攻擊速度",
            [30],
            ["crafted.stat_210067635", "crafted.stat_681332047"],
            "#% 增加攻擊速度"));

        var result = PobItemText.Build(item, InvariantStats);
        var lines = result.Text.Split('\n').Select(x => x.Trim()).ToList();

        Assert.Contains("30% increased Attack Speed", lines);
        Assert.DoesNotContain(lines, x => x.Contains("(Local)"));
        // 走的是「有干净模板可选」这条路，不该记成「剥掉注解」
        Assert.Equal(0, result.AnnotationStripped);
        Assert.Equal(1, result.MappedStats);
    }

    /// <summary>
    /// 兜底分支：整族候选**全带注解**时（实测格挡就是：5 条候选清一色 `(Local)`）剥掉注解再发。
    /// 剥掉注解仍比「让 PoB 整行忽略」好；剥掉之后 `+` 补号规则照旧生效（顺序不能反）。
    /// </summary>
    [Fact]
    public void Trailing_annotation_is_stripped_when_every_candidate_has_one()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "30% 增加格擋率", [30], ["explicit.stat_2481353198"], "#% 增加格擋率"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+40 護甲值", [40], ["explicit.stat_3484657501"], "+# 護甲值"));

        var result = PobItemText.Build(item, InvariantStats);
        var lines = result.Text.Split('\n').Select(x => x.Trim()).ToList();

        Assert.Contains("30% increased Block chance", lines);
        Assert.Contains("+40 to Armour", lines);   // 剥注解 + 补 `+`，两步都要在
        Assert.DoesNotContain(lines, x => x.Contains("(Local)"));
        Assert.Equal(2, result.AnnotationStripped);
        Assert.Equal(2, result.MappedStats);
        Assert.Equal(0, result.Skipped);
    }

    /// <summary>
    /// 稀有物品在 PoB 眼里是「名字 + 基底」两行，**只发一行会让 baseName 变 nil 并把 Lua 报错抛给用户**
    /// （实测 tao-charm-test.py：RARE + 单行 → `Classes/Item.lua:1867 attempt to concatenate field 'baseName'`；
    /// RARE + 同名两行 → 正常且词缀生效）。名称取不到时把基底重复一次当 title 行。
    /// </summary>
    [Fact]
    public void Rare_items_without_a_distinct_name_still_get_a_title_line()
    {
        var item = Helmet();
        item.InvariantTradeItem = new TradeItem { Type = "Kamasan Tiara" };   // 没有 Name

        var lines = PobItemText.Build(item, InvariantStats).Text.Split('\n').Select(x => x.Trim()).ToList();

        var baseLines = lines.Where(x => x == "Kamasan Tiara").ToList();
        Assert.Equal(2, baseLines.Count);                        // title 行 + 基底行
        Assert.Equal(1, lines.IndexOf("Kamasan Tiara"));         // 紧跟 Rarity 之后
    }

    /// <summary>反证：魔法物品本来就只有一行，不许被补出多余的 title 行。</summary>
    [Fact]
    public void Magic_items_are_not_given_an_extra_title_line()
    {
        var item = Helmet();
        item.Properties.Rarity = Rarity.Magic;
        item.InvariantTradeItem = new TradeItem { Type = "Kamasan Tiara" };

        var lines = PobItemText.Build(item, InvariantStats).Text.Split('\n').Select(x => x.Trim()).ToList();

        Assert.Equal(1, lines.Count(x => x == "Kamasan Tiara"));
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

    /// <summary>
    /// C2b 的变体构造：拿掉一条显式词缀 = 只少那一行，别的行一行不动。
    /// </summary>
    [Fact]
    public void WithoutAffix_removes_exactly_one_line()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));

        var built = PobItemText.Build(item, InvariantStats);
        var life = built.Affixes.Single(x => x.Text == "+88 to maximum Life");

        var variant = PobItemText.WithoutAffix(built.Text, life);

        Assert.NotNull(variant);
        Assert.DoesNotContain("+88 to maximum Life", variant);
        Assert.Contains("+45% to Fire Resistance", variant);      // 另一条词缀不受影响
        Assert.Contains("Kamasan Tiara", variant);                 // 头部信息也在
        Assert.Contains("Energy Shield: 436", variant);
    }

    /// <summary>
    /// 拿掉隐式词缀时 `Implicits: N` 必须跟着减一 —— 数量对不上时 PoB 不一定报错，
    /// 而是把显式词缀当隐式读（静默错读，比报错更难发现）。
    /// </summary>
    [Fact]
    public void WithoutAffix_decrements_the_implicits_declaration()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Implicit, "+30 最大能量护盾", [30], "implicit.stat_es", "+# 最大能量护盾"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));

        var built = PobItemText.Build(item, InvariantStats);
        Assert.Contains("Implicits: 1", built.Text);

        var implicitAffix = built.Affixes.Single(x => x.Implicit);
        var variant = PobItemText.WithoutAffix(built.Text, implicitAffix)!;

        // 最后一条隐式被拿掉 → 整条 Implicits 声明一起去掉（留着 "Implicits: 0" 反而会让 PoB 困惑）
        Assert.DoesNotContain("Implicits:", variant);
        Assert.DoesNotContain("+30 to maximum Energy Shield", variant);
        Assert.Contains("+45% to Fire Resistance", variant);
    }

    /// <summary>
    /// 拿掉显式词缀**不许**动到 `Implicits: N`（只有隐式才要减）。
    /// </summary>
    [Fact]
    public void WithoutAffix_keeps_the_implicits_declaration_for_explicit_affixes()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Implicit, "+30 最大能量护盾", [30], "implicit.stat_es", "+# 最大能量护盾"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));

        var built = PobItemText.Build(item, InvariantStats);
        var explicitAffix = built.Affixes.Single(x => !x.Implicit && x.Text == "+88 to maximum Life");

        var variant = PobItemText.WithoutAffix(built.Text, explicitAffix)!;

        Assert.Contains("Implicits: 1", variant);
        Assert.Contains("+30 to maximum Energy Shield", variant);
        Assert.DoesNotContain("+88 to maximum Life", variant);
    }

    /// <summary>
    /// 行号对不上文本时**返回 null**，不去别处找一条"看起来像"的行来删（宁可这条算不了）。
    /// 这条守卫的存在理由：调用方按 null 走"这条没算出来"的分支，而不是删错一行后得出错误差值。
    /// </summary>
    [Fact]
    public void WithoutAffix_refuses_when_the_line_number_does_not_match()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));

        var built = PobItemText.Build(item, InvariantStats);
        var affix = built.Affixes.Single();

        Assert.Null(PobItemText.WithoutAffix(built.Text, affix with { LineIndex = 999 }));
        Assert.Null(PobItemText.WithoutAffix(built.Text, affix with { Text = "+1 to something else" }));
    }

    /// <summary>
    /// **多行词缀**（审计 C2 阻断 A 的回归）：一条词缀占两个物理行时，
    /// 行号必须是**物理行号**、后续词缀的行号要跟着偏移；
    /// 拿掉它必须**整段删净**（只删第一行会把剩下半条留在文本里，PoB 会当成另一条词缀读 —— 数字全错且毫无提示）。
    /// </summary>
    [Fact]
    public void Multiline_affixes_get_physical_line_numbers_and_are_removed_as_a_block()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "击杀燃烧敌人有#%几率爆炸", [30], "explicit.stat_multiline", "#% 几率爆炸"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));

        var built = PobItemText.Build(item, InvariantStats);
        Assert.Equal(2, built.Affixes.Count);

        var multiline = built.Affixes[0];
        var life = built.Affixes[1];

        Assert.Equal(2, multiline.LineCount);                       // 占两个物理行
        Assert.Contains('\n', multiline.Text);
        Assert.Equal(1, life.LineCount);
        // 关键：后一条词缀的行号要**跨过**前一条占的两行，而不是按 `lines` 下标紧挨着
        Assert.Equal(multiline.LineIndex + 2, life.LineIndex);

        // 拿掉多行词缀：整段（两行）都消失，另一条词缀原样还在
        var withoutMultiline = PobItemText.WithoutAffix(built.Text, multiline)!;
        Assert.DoesNotContain("Burning Enemies", withoutMultiline);
        Assert.DoesNotContain("tenth of their maximum Life", withoutMultiline);   // 第二行也不许残留
        Assert.Contains("+88 to maximum Life", withoutMultiline);
        Assert.Equal(0, withoutMultiline.Split('\n').Count(x => x.StartsWith("tenth of")));

        // 拿掉单行词缀：多行词缀必须完好（两行都在）
        var withoutLife = PobItemText.WithoutAffix(built.Text, life)!;
        Assert.DoesNotContain("+88 to maximum Life", withoutLife);
        Assert.Contains("Burning Enemies", withoutLife);
        Assert.Contains("tenth of their maximum Life", withoutLife);
    }

    /// <summary>审计建议的单测 (b)：`Implicits: 2` 拿掉一条后必须是 `Implicits: 1`（不是删掉声明、也不是不变）。</summary>
    [Fact]
    public void WithoutAffix_decrements_a_multi_implicit_declaration()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Implicit, "+30 最大能量护盾", [30], "implicit.stat_es", "+# 最大能量护盾"));
        item.Stats.Add(Stat(StatCategory.Implicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));
        item.Stats.Add(Stat(StatCategory.Explicit, "+45% 火焰抗性", [45], "explicit.stat_fire_res", "+#% 火焰抗性"));

        var built = PobItemText.Build(item, InvariantStats);
        Assert.Contains("Implicits: 2", built.Text);

        var first = built.Affixes[0];
        var variant = PobItemText.WithoutAffix(built.Text, first)!;

        Assert.Contains("Implicits: 1", variant);
        Assert.DoesNotContain("Implicits: 2", variant);
        Assert.Contains("+88 to maximum Life", variant);       // 另一条隐式还在（它的行号也要跟着上移）
        Assert.Contains("+45% to Fire Resistance", variant);
    }

    /// <summary>
    /// 边界（审计第二轮 3-7）：**多行词缀是最后一条** + **没有隐式词缀**。
    /// `LineIndex + LineCount == 物理行数` 是**合法边界**（不是越界，不许被守卫拒掉），
    /// 且没有隐式时 `Implicit` 必须全为 false（否则 `Implicits:` 计数会被减花）。
    /// </summary>
    [Fact]
    public void Multiline_affix_as_the_last_line_without_implicits_is_handled()
    {
        var item = Helmet();
        item.Stats.Add(Stat(StatCategory.Explicit, "+88 最大生命", [88], "explicit.stat_life", "+# 最大生命"));
        item.Stats.Add(Stat(StatCategory.Explicit, "击杀燃烧敌人有#%几率爆炸", [30], "explicit.stat_multiline", "#% 几率爆炸"));

        var built = PobItemText.Build(item, InvariantStats);

        Assert.All(built.Affixes, x => Assert.False(x.Implicit));
        Assert.DoesNotContain("Implicits:", built.Text);

        var physicalLines = built.Text.TrimEnd('\n').Split('\n').Length;
        var last = built.Affixes[^1];
        Assert.Equal(2, last.LineCount);
        Assert.Equal(physicalLines - 2, last.LineIndex);      // 末尾那条占两行 → 起点 = 行数 − 2

        var variant = PobItemText.WithoutAffix(built.Text, last);
        Assert.NotNull(variant);                              // 边界不许被守卫误判成越界
        Assert.DoesNotContain("tenth of their maximum Life", variant);
        Assert.Contains("+88 to maximum Life", variant);
    }
}
