using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 内置词缀权重表（affix-weights.json）的根节点。
///
/// 这张表回答的是「这条词缀能不能出现在这个基底上」，用来算改造/洗装的期望成本。
/// 权重来自 PoB-PoE2 的重组器实验 + 交易站统计（社区逆向估算），**官方不公布成功率**，
/// 所以这里只当参考值用，界面必须把这一点说出来。
/// </summary>
public class AffixWeightFile
{
    /// <summary>数据来源说明。</summary>
    public string Source { get; set; } = "";

    /// <summary>生成方的备注。</summary>
    public string Note { get; set; } = "";

    /// <summary>全部词缀（前缀 + 后缀）。</summary>
    public List<ModWeight> Mods { get; set; } = [];
}

/// <summary>
/// 一条词缀的权重记录。
///
/// <see cref="W"/> 是 weightKey -&gt; weightVal 的字典。这一版导出的数据里权重只有 0 和 1，
/// 所以池内概率 = 条数比，不需要加权求和；但判定仍然按「权重 &gt; 0」来写，
/// 以后数据换成真实权重也不用改调用方。
///
/// 两个坑，别踩：
/// 1. <c>w["default"]</c> 恒为 0，语义是「不在列举基底里的基底就不出这条」——
///    建池必须用物品的全部标签，不能拿 default 当兜底；
/// 2. <c>w</c> 里还有权重恒 0 的排除标记（no_fire_spell_mods 等）和机制键（genesis_tree_*），
///    按「权重 &gt; 0 才算」处理就不用特判。
/// </summary>
public class ModWeight
{
    /// <summary>词缀 id，例如 "FireResist1" / "IncreasedLife3"。</summary>
    public string Id { get; set; } = "";

    /// <summary>原始类型字符串："Prefix" / "Suffix"。</summary>
    public string Type { get; set; } = "";

    /// <summary>该词缀的 ilvl 门槛：池里只收 level &lt;= 物品等级的。</summary>
    public int Level { get; set; }

    /// <summary>词缀组，例如 "FireResistance" / "IncreasedLife"。</summary>
    public string Group { get; set; } = "";

    /// <summary>词缀文本（含数值区间），例如 "+(70-84) to maximum Life"。</summary>
    public string Text { get; set; } = "";

    /// <summary>weightKey -&gt; weightVal。取值 0/1。</summary>
    public Dictionary<string, int> W { get; set; } = [];

    /// <summary>解析成枚举；数据里出现别的写法按「未知」处理，不猜。</summary>
    public AffixSide Side => Type?.Trim().ToLowerInvariant() switch
    {
        "prefix" => AffixSide.Prefix,
        "suffix" => AffixSide.Suffix,
        _ => AffixSide.Unknown,
    };

    /// <summary>这条词缀的 weightKey 里有没有这个标签（且权重大于 0）。</summary>
    public bool HasWeight(string? tag) =>
        !string.IsNullOrWhiteSpace(tag)
        && W.TryGetValue(tag, out var weight)
        && weight > 0;

    /// <summary>
    /// 这条词缀能不能出现在这个标签集的池里：**任一**标签权重大于 0 就算（并集，不是交集）。
    /// 例如抗性词缀挂的是 {armour, ring, amulet, belt}，鞋子能出抗性靠的就是 armour 这把伞。
    /// </summary>
    public bool SpawnsOn(IReadOnlyCollection<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (HasWeight(tag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>「+(70-84) to maximum Life」这类括号区间。</summary>
    private static readonly Regex BracketRange = new(
        @"\(\s*(\d+(?:\.\d+)?)\s*-\s*(\d+(?:\.\d+)?)\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex AnyNumber = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);

    private bool upperBoundParsed;
    private double? upperBound;

    /// <summary>
    /// 文本里的数值上限（用来判「这条词缀的数值够不够用户目标」）。
    ///
    /// 有括号区间就取各区间上限的最大值（"+(70-84)" → 84），没有括号的单值取最大数字
    /// （"10% increased Movement Speed" → 10）。解析不出数字为 null，调用方按「判定不了」处理。
    /// </summary>
    public double? UpperBound
    {
        get
        {
            if (!upperBoundParsed)
            {
                upperBound = ParseUpperBound(Text);
                upperBoundParsed = true;
            }

            return upperBound;
        }
    }

    private static double? ParseUpperBound(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var brackets = BracketRange.Matches(text);
        if (brackets.Count > 0)
        {
            var max = double.NegativeInfinity;
            foreach (Match match in brackets)
            {
                if (double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value > max)
                {
                    max = value;
                }
            }

            return double.IsNegativeInfinity(max) ? null : max;
        }

        var numbers = AnyNumber.Matches(text);
        if (numbers.Count == 0)
        {
            return null;
        }

        var single = double.NegativeInfinity;
        foreach (Match match in numbers)
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && value > single)
            {
                single = value;
            }
        }

        return double.IsNegativeInfinity(single) ? null : single;
    }

    private string? normalizedText;

    /// <summary>归一化后的词缀文本（缓存）。</summary>
    public string NormalizedText => normalizedText ??= Normalize(Text);

    /// <summary>
    /// 只保留字母（含中日韩文字）并转小写：数值、百分号、括号、连字符、"#" 占位符全部丢掉。
    ///
    /// 词缀库里的模式写「Adds # to # Fire Damage」，权重表里的文本写
    /// 「Adds (5-8) to (11-14) Fire Damage」，去噪之后两边都能对上；
    /// 中英混排的关键词也能用同一条规则比较。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// 词缀权重表的加载结果：要么拿到数据，要么带着「为什么没有」的原因，绝不静默失败。
/// </summary>
public class AffixWeightSnapshot
{
    public AffixWeightFile? Data { get; init; }

    public string? LoadedFrom { get; init; }

    public string? LoadError { get; init; }

    public IReadOnlyList<string> SearchedPaths { get; init; } = [];

    public bool HasData => Data != null;
}

/// <summary>
/// 词缀池的标签集（weightKey 集合）：用哪些标签去权重表里建池。
///
/// <see cref="Degraded"/> = true 表示拿不到防御值，池只按「槽位 + armour」估算，
/// 会比真实池小、概率偏大、成本偏低 —— 界面必须把这条说出来。
/// </summary>
public sealed record AffixPoolTagSet(IReadOnlyList<string> Tags, bool Degraded, string? Reason = null)
{
    public bool IsEmpty => Tags.Count == 0;

    /// <summary>展示用：「boots / armour / dex_armour」。</summary>
    public string TagText => string.Join(" / ", Tags);
}

/// <summary>
/// 「装备 -&gt; 词缀池标签集」的映射。
///
/// 规则（与权重表的 weightKey 对齐）：
///   ⚠ 已知缺口（审计 + 复审 2026-09-19 记录在案，未擅自补 —— 猜错会把不该显示的词缀放出来）：
///     · `ranged`   28 条 —— 语义未定（不传武器类别的调用路径会漏）
///     · `talisman` 47 条 —— 护身符槽位，本工具目前没有该槽位
///     · `trap`    136 条 —— 陷阱技能相关，本工具不涉及技能宝石
///     · `genesis_tree_*` 83 + 68 条 —— 0.5 赛季机制专用；**注意它们权重是 1，不是 0**
///   这四类都只在「用户恰好需要它们」时才成为问题；目前不做也不影响加法类词缀的结论。
///
///   1. 槽位键：helmet | body_armour | gloves | boots | belt | amulet | ring | shield | focus | quiver；
///   2. 护甲槽位（helmet / body_armour / gloves / boots / shield）**额外加 armour 这把伞**——
///      抗性之类的词缀挂的就是 {armour, ring, amulet, belt}，只看 boots 会漏掉一大堆，
///      池子偏小 → 概率偏大 → 成本低估；
///   3. 护甲子类：用物品上解析出来的防御值推导（str/dex/int 七种组合），**不按基底名查表**；
///   4. 退化路径：防御值全为 0（拿不到）时只用 {槽位, armour}，并标记 <see cref="AffixPoolTagSet.Degraded"/>，
///      不静默按某种子类猜。
/// </summary>
public static class AffixPoolTags
{
    /// <summary>护甲槽位共用的伞形标签。</summary>
    public const string Armour = "armour";

    public const string Helmet = "helmet";
    public const string BodyArmour = "body_armour";
    public const string Gloves = "gloves";
    public const string Boots = "boots";
    public const string Belt = "belt";
    public const string Amulet = "amulet";
    public const string Ring = "ring";
    public const string Shield = "shield";
    public const string Focus = "focus";
    public const string Quiver = "quiver";

    /// <summary>武器共用的伞形标签（和 armour 一样，权重表里是"任意武器"的通配）。</summary>
    public const string Weapon = "weapon";

    public const string OneHandWeapon = "one_hand_weapon";
    public const string TwoHandWeapon = "two_hand_weapon";

    /// <summary>物品类别 -&gt; 槽位键。</summary>
    private static readonly Dictionary<ItemClass, string> SlotKeyByClass = new()
    {
        [ItemClass.Helmet] = Helmet,
        [ItemClass.BodyArmour] = BodyArmour,
        [ItemClass.Gloves] = Gloves,
        [ItemClass.Boots] = Boots,
        [ItemClass.Belt] = Belt,
        [ItemClass.Amulet] = Amulet,
        [ItemClass.Ring] = Ring,
        [ItemClass.Shield] = Shield,
        // 圆盾在 PoE2 里就是盾的一个类别，权重表里没有单独的 buckler 键。
        [ItemClass.Buckler] = Shield,
        [ItemClass.Focus] = Focus,
        [ItemClass.Quiver] = Quiver,
    };

    /// <summary>
    /// 物品类别 -&gt; 权重表里的具体武器类型键。
    /// 注意权重表只按"剑/斧/锤"分，不区分单手双手（sword / axe / mace 两类共用），
    /// 所以单手双手要靠 one_hand_weapon / two_hand_weapon 这两个伞形键补。
    /// </summary>
    private static readonly Dictionary<ItemClass, string> WeaponKeyByClass = new()
    {
        [ItemClass.Bow] = "bow",
        [ItemClass.Crossbow] = "crossbow",
        [ItemClass.Claw] = "claw",
        [ItemClass.Dagger] = "dagger",
        [ItemClass.OneHandAxe] = "axe",
        [ItemClass.TwoHandAxe] = "axe",
        [ItemClass.OneHandMace] = "mace",
        [ItemClass.TwoHandMace] = "mace",
        [ItemClass.OneHandSword] = "sword",
        [ItemClass.TwoHandSword] = "sword",
        [ItemClass.Flail] = "flail",
        [ItemClass.Sceptre] = "sceptre",
        [ItemClass.Staff] = "staff",
        [ItemClass.Warstaff] = "warstaff",
        [ItemClass.Wand] = "wand",
        [ItemClass.Spear] = "spear",
        [ItemClass.FishingRod] = "fishing_rod",
    };

    /// <summary>拿单手武器伞形键的类别。</summary>
    private static readonly HashSet<ItemClass> OneHandClasses =
    [
        ItemClass.Claw, ItemClass.Dagger, ItemClass.OneHandAxe, ItemClass.OneHandMace,
        ItemClass.OneHandSword, ItemClass.Flail, ItemClass.Sceptre, ItemClass.Wand,
    ];

    /// <summary>按武器类型键就能确定是双手的（剑/斧/锤两类共用键，不在其中）。</summary>
    private static readonly HashSet<string> TwoHandByKey =
        ["bow", "crossbow", "staff", "warstaff", "spear", "fishing_rod"];

    /// <summary>按武器类型键就能确定是单手的（剑/斧/锤两类共用键，不在其中）。</summary>
    private static readonly HashSet<string> OneHandByKey =
        ["wand", "sceptre", "claw", "dagger", "flail"];

    /// <summary>拿双手武器伞形键的类别。</summary>
    private static readonly HashSet<ItemClass> TwoHandClasses =
    [
        ItemClass.Bow, ItemClass.Crossbow, ItemClass.Staff, ItemClass.Warstaff,
        ItemClass.TwoHandAxe, ItemClass.TwoHandMace, ItemClass.TwoHandSword,
        ItemClass.Spear, ItemClass.FishingRod,
    ];

    /// <summary>能用 armour 这把伞的槽位（护甲类）。</summary>
    private static readonly HashSet<string> ArmourSlots = [Helmet, BodyArmour, Gloves, Boots, Shield];

    /// <summary>防御值推导子类的槽位（护甲类 + 法器：法器有能量护盾，权重表里也带 int_armour）。</summary>
    private static readonly HashSet<string> DefenceSlots = [Helmet, BodyArmour, Gloves, Boots, Shield, Focus];

    /// <summary>界面槽位键 -&gt; 槽位键（物品类别解析不出来时的兜底）。</summary>
    private static readonly Dictionary<string, string> SlotKeyBySlot = new()
    {
        [SlotKeys.Helmet] = Helmet,
        [SlotKeys.BodyArmour] = BodyArmour,
        [SlotKeys.Gloves] = Gloves,
        [SlotKeys.Boots] = Boots,
        [SlotKeys.Belt] = Belt,
        [SlotKeys.Amulet] = Amulet,
        [SlotKeys.Ring1] = Ring,
        [SlotKeys.Ring2] = Ring,
    };

    /// <summary>按物品解析结果建标签集。</summary>
    public static AffixPoolTagSet Resolve(Item? item)
    {
        var properties = item?.Properties;
        var slotKey = SlotKeyOf(item);

        return Resolve(
            slotKey,
            properties?.Armour ?? 0,
            properties?.EvasionRating ?? 0,
            properties?.EnergyShield ?? 0,
            item?.ItemClass?.Type);
    }

    /// <summary>物品类别解析不出来时，用界面槽位键兜底。</summary>
    public static AffixPoolTagSet Resolve(Item? item, string? slotKey)
    {
        var resolved = SlotKeyOf(item);
        if (resolved is null && !string.IsNullOrWhiteSpace(slotKey))
        {
            SlotKeyBySlot.TryGetValue(slotKey, out resolved);
        }

        var properties = item?.Properties;

        return Resolve(
            resolved,
            properties?.Armour ?? 0,
            properties?.EvasionRating ?? 0,
            properties?.EnergyShield ?? 0,
            item?.ItemClass?.Type);
    }

    /// <summary>核心映射：槽位键 + 三个防御值 -&gt; 标签集。</summary>
    public static AffixPoolTagSet Resolve(
        string? slotKey,
        int armour,
        int evasion,
        int energyShield,
        ItemClass? itemClass = null)
    {
        if (string.IsNullOrWhiteSpace(slotKey))
        {
            return new AffixPoolTagSet([], Degraded: false, "slot-unknown");
        }

        var tags = new List<string> { slotKey };

        if (ArmourSlots.Contains(slotKey))
        {
            tags.Add(Armour);
        }

        // 武器：补 weapon 伞形键 + 单手/双手伞形键，然后收尾（武器没有防御值子类）。
        if (WeaponKeyByClass.ContainsValue(slotKey))
        {
            tags.Add(Weapon);

            if (OneHandClasses.Contains(itemClass ?? ItemClass.Unknown))
            {
                tags.Add(OneHandWeapon);
            }
            else if (TwoHandClasses.Contains(itemClass ?? ItemClass.Unknown))
            {
                tags.Add(TwoHandWeapon);
            }
            else if (TwoHandByKey.Contains(slotKey))
            {
                // 类别拿不到（界面槽位兜底路径）：按武器类型键推，推不出来就不加，不猜。
                tags.Add(TwoHandWeapon);
            }
            else if (OneHandByKey.Contains(slotKey))
            {
                tags.Add(OneHandWeapon);
            }

            return new AffixPoolTagSet(tags, Degraded: false);
        }

        if (!DefenceSlots.Contains(slotKey))
        {
            // 项链 / 戒指 / 腰带 / 箭袋本来就没有防御值，不存在退化问题。
            return new AffixPoolTagSet(tags, Degraded: false);
        }

        var subtype = DefenceSubtype(armour, evasion, energyShield);
        if (subtype is null)
        {
            // 拿不到防御值：只说「这个槽位能出什么」，池会比真实的小，结果偏乐观。
            return new AffixPoolTagSet(tags, Degraded: true, "defence-unknown");
        }

        tags.Add(subtype);

        // 盾牌的防御子类键和护甲部位不是同一族：
        // 头盔/胸甲/手套/鞋用 str_armour 那一族，盾牌专用词缀挂的是 str_shield / str_dex_shield / str_int_shield。
        // 漏了这三个键的后果是静默的 —— 盾牌能出的 16 条词缀（含「+X% 全部元素抗性」）会被搜索过滤藏掉，
        // 界面上不会有任何提示；同一标签集进成本池还会让期望成本偏低。审计（2026-09-19）发现。
        if (slotKey == Shield && ShieldSubtype(armour, evasion, energyShield) is { } shieldSubtype)
        {
            tags.Add(shieldSubtype);
        }

        return new AffixPoolTagSet(tags, Degraded: false);
    }

    /// <summary>
    /// 盾牌的防御子类键。数据里盾牌专用词缀只挂了 str_shield / str_dex_shield / str_int_shield 三种，
    /// 没有纯敏 / 纯智 / 三属性盾的 shield 键 —— 不硬造，那几种盾交给 armour 伞和 *_armour 一族兜底。
    /// 与 <see cref="DefenceSubtype"/> 分开写是故意的：两族的键名不同，合并会让人以为可以互换。
    /// </summary>
    private static string? ShieldSubtype(int armour, int evasion, int energyShield)
    {
        var hasArmour = armour > 0;
        var hasEvasion = evasion > 0;
        var hasEnergyShield = energyShield > 0;

        return (hasArmour, hasEvasion, hasEnergyShield) switch
        {
            (true, false, false) => "str_shield",
            (true, true, false) => "str_dex_shield",
            (true, false, true) => "str_int_shield",
            _ => null,
        };
    }

    private static string? SlotKeyOf(Item? item)
    {
        var type = item?.ItemClass?.Type ?? ItemClass.Unknown;
        if (type == ItemClass.Unknown)
        {
            return null;
        }

        if (SlotKeyByClass.TryGetValue(type, out var slotKey))
        {
            return slotKey;
        }

        // 武器：权重表按具体类型分开（wand / bow / sword ...），另有 weapon 伞形键。
        return WeaponKeyByClass.TryGetValue(type, out var weaponKey) ? weaponKey : null;
    }

    /// <summary>
    /// 防御值 -&gt; 护甲子类标签。全 0（或负数）视为「拿不到」，返回 null 交给退化路径。
    /// </summary>
    private static string? DefenceSubtype(int armour, int evasion, int energyShield)
    {
        var hasArmour = armour > 0;
        var hasEvasion = evasion > 0;
        var hasEnergyShield = energyShield > 0;

        return (hasArmour, hasEvasion, hasEnergyShield) switch
        {
            (true, false, false) => "str_armour",
            (false, true, false) => "dex_armour",
            (false, false, true) => "int_armour",
            (true, true, false) => "str_dex_armour",
            (true, false, true) => "str_int_armour",
            (false, true, true) => "dex_int_armour",
            (true, true, true) => "str_dex_int_armour",
            _ => null,
        };
    }
}
