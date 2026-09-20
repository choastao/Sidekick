using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sidekick.Game.ItemClasses;
using Sidekick.Game.Parser.Items;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 内置词缀权重表（affix-weights.json）的根节点。
///
/// 这张表回答的是「这条词缀能不能出现在这个基底上」，现在只用来给词缀浏览 / 搜索建池
/// （概率与期望成本已经改用带真实权重的 <see cref="AffixPoolCoeFile"/>）。
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
/// 所以这份数据只够回答「能不能出」——**别拿它算概率**（那会退化成数条数）；
/// 概率走 <see cref="AffixPoolCoeFile"/> 的真实权重，池也按底材建。
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
/// 词缀池的标签集（weightKey 集合）。
///
/// ⚠ v3.6 起两处用途要分清：
///   · <b>概率/成本</b>已改为按 CoE 底材 id 取池（见 AffixPoolCoEService），**不再用本标签集建池**，
///     所以标签集不参与任何概率计算；
///   · 本标签集仍用于**词缀搜索过滤**（StatPicker / AffixPoolStatFilter）。
/// <see cref="Degraded"/> = true 表示拿不到防御值，标签集是完整标签集的**子集**
/// （少了 str/dex/int 那些防御子类键）→ 搜索过滤按「任一标签命中」取并集，所以结果**只会漏、不会多列**。
/// 用户可见文案 `Search_Filter_Degraded`（「护甲子类词缀可能被漏掉」）就是这个方向 —— 别写成相反。
/// 该标记只对搜索过滤成立，不要拿去描述概率。
///
/// <see cref="NoSlotFilter"/> = true 表示**这个类别本来就不做部位过滤**（药剂 / 咒符 / 珠宝）。
/// 与 <see cref="Degraded"/>、与 <see cref="IsEmpty"/> 都不是一回事，界面必须分开说：
///   · `IsEmpty` + 不带本标记 = 认不出部位（可以说「认不出这件装备」）；
///   · 带本标记 = 部位认得出来、但权重表没有这个部位的标签，**过滤本身不适用**
///     （说「认不出装备」是假话，见 search_filter 文案 `Search_Filter_NoSlotTags`）。
/// 这类别**不影响概率/成本**：池按 CoE 底材 id 取（药剂 60/61、咒符 241、珠宝 26/27/28），照常算得出。
/// </summary>
public sealed record AffixPoolTagSet(
    IReadOnlyList<string> Tags,
    bool Degraded,
    string? Reason = null,
    bool NoSlotFilter = false)
{
    public bool IsEmpty => Tags.Count == 0;

    /// <summary>展示用：「boots / armour / dex_armour」。</summary>
    public string TagText => string.Join(" / ", Tags);
}

/// <summary>
/// 「装备 -&gt; 词缀池标签集」的映射。
///
/// 规则（与权重表的 weightKey 对齐）：
///   ⚠ 已知缺口（三轮审计/复审 2026-09-19 记录在案，未擅自补 —— 猜错会把不该显示的词缀放出来）。
///   注意「数据里有这个键」≠「这是缺口」：只有**在某个我们能遇到的槽位下、除了它没有别的键能匹配**的才算缺口。
///   按这个口径，真正的缺口只有 **1 条**（`ranged`，ilvl82 的最高命中档）；其余是整族不适用：
///     · `trap`     136 条 —— 陷阱技能相关，本工具不涉及技能宝石
///     · `talisman`  46 条 —— 护身符槽位，本工具目前没有该槽位
///     · `genesis_tree_*` 83 + 68 条 —— 0.5 赛季机制专用；**权重是 1，不是 0**（旧注释写错过）
///     · 另有 `soul` / `berserking` / `marksman` 等 7 族键 —— 同属「整族不适用于当前槽位」
///   结论：不做也不影响加法类词缀的判定。
///
///   1. 槽位键：helmet | body_armour | gloves | boots | belt | amulet | ring | shield | focus | quiver；
///   2. 护甲槽位（helmet / body_armour / gloves / boots / shield）**额外加 armour 这把伞**——
///      抗性之类的词缀挂的就是 {armour, ring, amulet, belt}，只看 boots 会漏掉一大堆
///      （影响的是**搜索过滤**召回；概率池按 CoE 底材 id 取，不走标签，见下面第 4 条）；
///   3. 护甲子类：用物品上解析出来的防御值推导（str/dex/int 七种组合），**不按基底名查表**；
///   4. 退化路径：防御值全为 0（拿不到）时只用 {槽位, armour}，并标记 <see cref="AffixPoolTagSet.Degraded"/>，
///      不静默按某种子类猜。⚠ v3.6 起这个标记**只影响搜索过滤的精度提示**，与概率/成本无关
///      （概率池按 CoE 底材 id 取，见 AffixPoolCoEService）。
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

    /// <summary>
    /// 药剂 / 咒符 / 珠宝：`affix-weights.json` 的 60 个标签里**没有** flask / charm / jewel，
    /// 给这三个类别配一个匹配不到任何词缀的标签，只会把词缀搜索过滤成空列表（看起来像功能坏了）。
    /// 所以这三个部位**显式不做部位过滤**，理由是「权重表没有这个部位的标签」，
    /// 而不是「认不出这件装备」—— 后者是假话，界面文案必须分开。
    /// </summary>
    public const string NoSlotFilterReason = "no-slot-tags";

    /// <summary>不做部位过滤的类别（判定见 <see cref="Resolve(string?, int, int, int, ItemClass?)"/>）。</summary>
    private static bool IsNoSlotFilterCategory(ItemClass? itemClass) =>
        itemClass is ItemClass.LifeFlask or ItemClass.ManaFlask or ItemClass.Charms or ItemClass.Jewel;

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

    // ---- 物品 -> Craft of Exile 底材 id（affix-pool-coe.json 的 bases 键） ----

    /// <summary>
    /// 物品类别就能定死底材的：首饰、法器、护身符、以及「按类型分底材」的武器。
    ///
    /// 剑 / 斧 / 锤在 CoE 数据里单手双手是**不同底材**（13 vs 22、15 vs 24、16 vs 23），
    /// 所以这里必须用我们解析出来的具体类别，不能用 weightKey 那套「sword / axe / mace」。
    /// </summary>
    private static readonly Dictionary<ItemClass, string> BaseByClass = new()
    {
        [ItemClass.Belt] = "3",
        [ItemClass.Ring] = "1",
        [ItemClass.Amulet] = "2",
        [ItemClass.Quiver] = "4",
        [ItemClass.Focus] = "229",
        [ItemClass.Talisman] = "244",

        // 药剂 / 咒符：CoE 对这两类是**类别级**的单个底材（游戏里生命药剂 9 个底子、
        // 咒符 13 种，CoE 各只有 1 个）→ 是近似，界面按类别近似标注，别写成精确。
        [ItemClass.LifeFlask] = "60",
        [ItemClass.ManaFlask] = "61",
        [ItemClass.Charms] = "241",

        [ItemClass.Claw] = "11",
        [ItemClass.Dagger] = "12",
        [ItemClass.OneHandSword] = "13",
        [ItemClass.TwoHandSword] = "22",
        [ItemClass.OneHandAxe] = "15",
        [ItemClass.TwoHandAxe] = "24",
        [ItemClass.OneHandMace] = "16",
        [ItemClass.TwoHandMace] = "23",
        [ItemClass.Sceptre] = "17",
        [ItemClass.Wand] = "18",
        [ItemClass.Spear] = "216",
        [ItemClass.Flail] = "217",
        [ItemClass.Bow] = "20",
        [ItemClass.Staff] = "21",
        [ItemClass.Warstaff] = "25",
        [ItemClass.Crossbow] = "228",
    };

    /// <summary>
    /// 珠宝：CoE 只有红/绿/蓝宝石三个底材，按**基底名**取（不能按类别，类别下 9 个底子混在一起）。
    /// 精确匹配，不做前缀匹配 —— 「時迭藍寶石 / Time-Lost Sapphire」是另一个底材，
    /// CoE 里没有对应项，必须返回 null（按「池不可用」如实说），**不许退到一般藍寶石上**。
    /// </summary>
    private static readonly Dictionary<string, string> JewelBaseByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ruby"] = "26",
        ["紅寶石"] = "26",
        ["红宝石"] = "26",
        ["Emerald"] = "27",
        ["綠寶石"] = "27",
        ["绿宝石"] = "27",
        ["Sapphire"] = "28",
        ["藍寶石"] = "28",
        ["蓝宝石"] = "28",
    };

    /// <summary>类别拿不到时，靠界面槽位键兜底的首饰类底材。</summary>
    private static readonly Dictionary<string, string> BaseBySlot = new()
    {
        [Belt] = "3",
        [Ring] = "1",
        [Amulet] = "2",
        [Quiver] = "4",
        [Focus] = "229",
    };

    /// <summary>
    /// 护甲部位 / 盾牌：底材随「属性组合」而变，属性组合由物品上解析出来的防御值推导。
    /// 键里的属性顺序固定为 str / dex / int（与 <see cref="DefenceSubtype"/> 一致）。
    /// </summary>
    private static readonly Dictionary<(string Slot, string Attrs), string> BaseBySlotAndAttrs = new()
    {
        [(Shield, "str")] = "5",
        [(Shield, "dex")] = "6",
        [(Shield, "str_dex")] = "8",
        [(Shield, "str_int")] = "9",

        [(BodyArmour, "str")] = "45",
        [(BodyArmour, "dex")] = "46",
        [(BodyArmour, "int")] = "47",
        [(BodyArmour, "str_dex")] = "48",
        [(BodyArmour, "str_int")] = "49",
        [(BodyArmour, "dex_int")] = "50",

        [(Boots, "str")] = "39",
        [(Boots, "dex")] = "40",
        [(Boots, "int")] = "41",
        [(Boots, "str_dex")] = "42",
        [(Boots, "str_int")] = "43",
        [(Boots, "dex_int")] = "44",

        [(Gloves, "str")] = "33",
        [(Gloves, "dex")] = "34",
        [(Gloves, "int")] = "35",
        [(Gloves, "str_dex")] = "36",
        [(Gloves, "str_int")] = "37",
        [(Gloves, "dex_int")] = "38",

        [(Helmet, "str")] = "52",
        [(Helmet, "dex")] = "53",
        [(Helmet, "int")] = "54",
        [(Helmet, "str_dex")] = "55",
        [(Helmet, "str_int")] = "56",
        [(Helmet, "dex_int")] = "57",
    };

    /// <summary>
    /// 物品 -&gt; <c>affix-pool-coe.json</c> 里的底材 id（概率池是按**底材**取的，不是按标签集）。
    ///
    /// 为什么要按底材而不是标签集：同一个词缀族在不同底材上的权重可以不同
    /// （例：Dexterity 在纯 DEX 底材是 1000、在 DEX/INT 底材是 500），
    /// 用标签汇总会把权重算错。
    ///
    /// **判不出来就返回 null**（缺属性、三属性护甲、非常见底材、单手双手分不清的剑/斧/锤），
    /// 调用方按「池不可用」处理，不猜一个近似的底材顶上。
    /// </summary>
    public static string? ResolveBase(Item? item, string? slotKey)
    {
        var type = item?.ItemClass?.Type ?? ItemClass.Unknown;

        // 珠宝：类别下混着 9 个底子，只能按基底名取（见 JewelBaseByName 的注释）。
        if (type == ItemClass.Jewel)
        {
            var name = item?.Type?.Trim();
            return name != null && JewelBaseByName.TryGetValue(name, out var jewelBase) ? jewelBase : null;
        }

        if (BaseByClass.TryGetValue(type, out var fixedBase))
        {
            return fixedBase;
        }

        var resolved = SlotKeyOf(item);
        if (resolved is null && !string.IsNullOrWhiteSpace(slotKey))
        {
            SlotKeyBySlot.TryGetValue(slotKey, out resolved);
        }

        if (resolved is null)
        {
            return null;
        }

        if (BaseBySlot.TryGetValue(resolved, out var slotBase))
        {
            return slotBase;
        }

        var properties = item?.Properties;
        var armour = properties?.Armour ?? 0;
        var evasion = properties?.EvasionRating ?? 0;
        var energyShield = properties?.EnergyShield ?? 0;

        // 属性组合由物品上解析出来的防御值推导（str / dex / int 的七种组合之一）。
        // 盾牌在这里走同一套：CoE 的盾牌底材是 5 / 6 / 8 / 9（str / dex / str+dex / str+int），
        // 和权重表里 str_shield 那一族的键名不是一回事，所以不能复用 ShieldSubtype。
        var subtype = DefenceSubtype(armour, evasion, energyShield);
        if (subtype is null)
        {
            return null;
        }

        var attrs = subtype[..^"_armour".Length];

        return BaseBySlotAndAttrs.GetValueOrDefault((resolved, attrs));
    }

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
        // 药剂 / 咒符 / 珠宝：权重表没有这三个部位的标签，配标签集 = 把搜索过滤成空列表。
        // 显式声明「不做部位过滤」，理由与「认不出部位」分开（见 NoSlotFilterReason 的注释）。
        if (IsNoSlotFilterCategory(itemClass))
        {
            return new AffixPoolTagSet([], Degraded: false, NoSlotFilterReason, NoSlotFilter: true);
        }

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
            // 拿不到防御值：只说「这个槽位能出什么」—— 搜索过滤会**漏掉**防御子类专属词缀（不会多列）。
            // 概率池不走这条路径（按 CoE 底材 id 取），所以这里只影响搜索列表的召回。
            return new AffixPoolTagSet(tags, Degraded: true, "defence-unknown");
        }

        tags.Add(subtype);

        // 盾牌的防御子类键和护甲部位不是同一族：
        // 头盔/胸甲/手套/鞋用 str_armour 那一族，盾牌专用词缀挂的是 str_shield / str_dex_shield / str_int_shield。
        // 漏了这三个键的后果是静默的 —— 盾牌能出的 16 条词缀（含「+X% 全部元素抗性」）会被搜索过滤藏掉，
        // 界面上不会有任何提示。审计（2026-09-19）发现。
        // （v3.6：概率池已改为按 CoE 底材 id 取，不再受标签集影响。）
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
