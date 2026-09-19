using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 词缀位于装备的哪一侧。决定配哪一张预兆（左旋 = 前缀，右旋 = 后缀）。
/// </summary>
public enum AffixSide
{
    /// <summary>数据里没有这条词缀（或没载入数据）——界面显示「（类型未知）」，不猜。</summary>
    Unknown = 0,

    Prefix = 1,

    Suffix = 2,
}

/// <summary>
/// 内置词缀类型表（affix-types.json）的根节点。随分发包发布，离线可用。
///
/// 数据来自 PoB-PoE2 的 src/Data/ModItem.lua（游戏数据导出），只有这里才能可靠地拿到
/// 「这条词缀是前缀还是后缀」——物品解析结果分不出来，猜错会把预兆建议指反。
/// </summary>
public class AffixTypeFile
{
    /// <summary>数据来源说明。</summary>
    public string Source { get; set; } = "";

    /// <summary>生成方的备注。</summary>
    public string Note { get; set; } = "";

    /// <summary>来源里扫到的词缀总数（仅作参考）。</summary>
    public int ModCount { get; set; }

    /// <summary>预设 Key -&gt; 词缀类型。Key 与 <see cref="StatPresets"/> 一致。</summary>
    public Dictionary<string, AffixTypePreset> Presets { get; set; } = [];
}

/// <summary>一条预设词缀的类型信息。</summary>
public class AffixTypePreset
{
    /// <summary>显示名，例如「最大生命」。</summary>
    public string Label { get; set; } = "";

    /// <summary>原始类型字符串："Prefix" / "Suffix"。</summary>
    public string Type { get; set; } = "";

    /// <summary>tier 数量。</summary>
    public int TierCount { get; set; }

    /// <summary>最低 tier 的 ilvl 门槛。</summary>
    public int MinLevel { get; set; }

    /// <summary>最高 tier 的 ilvl 门槛。</summary>
    public int MaxLevel { get; set; }

    /// <summary>英文关键词（用来和物品行做匹配）。</summary>
    public string EnglishKeyword { get; set; } = "";

    /// <summary>归一化后的英文模式（小写）。</summary>
    public string NormPattern { get; set; } = "";

    /// <summary>各 tier 明细（数值区间 / ilvl 门槛）。</summary>
    public List<AffixTypeTier> Tiers { get; set; } = [];

    private List<AffixTierHit>? tierLadder;

    /// <summary>
    /// 按 ilvl 升序排好的 tier 梯子，附带展示用等阶号（T1 起）。只含数值区间解析得出的 tier；
    /// 解析不出来的 tier 不参与判定（调用方走「无法判定等阶上限」的退化路径）。
    ///
    /// 数据是聚合的：同一条词缀在不同部位类别下是各自独立的梯子（例如「全局能量护盾」和
    /// 「本地能量护盾」），所以等阶号在一条梯子（group）内部单独编，不跨梯子混着数。
    /// </summary>
    public IReadOnlyList<AffixTierHit> TierLadder => tierLadder ??= BuildTierLadder();

    /// <summary>
    /// 覆盖当前值的 tier。空结果 = 数据缺失 / 该 tier 的数值区间解析不出来。
    /// 同一条词缀可能有多条梯子同时覆盖该值（全局 / 本地等），由调用方决定怎么取。
    /// </summary>
    public IReadOnlyList<AffixTierHit> FindCoveringTiers(double current) =>
        [.. TierLadder.Where(x => current >= x.Min && current <= x.Max)];

    private List<AffixTierHit> BuildTierLadder()
    {
        var ladder = new List<AffixTierHit>();

        // GroupBy 保留首次出现顺序；OrderBy 是稳定排序，同 ilvl 的 tier 保持原始顺序。
        foreach (var group in Tiers.GroupBy(x => x.Group))
        {
            var ordered = group.OrderBy(x => x.Level).ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Range is not { } range)
                {
                    // 数值区间解析不出来：这一步判不出上限，交给调用方按「无法判定」处理。
                    continue;
                }

                ladder.Add(new AffixTierHit(ordered[i], i + 1, range.Min, range.Max));
            }
        }

        return ladder;
    }

    /// <summary>解析成枚举；数据里出现别的写法按「未知」处理，不猜。</summary>
    public AffixSide Side => Type?.Trim().ToLowerInvariant() switch
    {
        "prefix" => AffixSide.Prefix,
        "suffix" => AffixSide.Suffix,
        _ => AffixSide.Unknown,
    };
}

/// <summary>一条 tier 在它自己那条梯子里的位置（T1 起）与数值区间。</summary>
public sealed record AffixTierHit(AffixTypeTier Tier, int Number, double Min, double Max);

/// <summary>一条 tier。</summary>
public class AffixTypeTier
{
    /// <summary>该 tier 的 ilvl 门槛。</summary>
    public int Level { get; set; }

    /// <summary>该 tier 的文本（含数值区间），例如 "+(10-19) to maximum Life"。</summary>
    public string Text { get; set; } = "";

    /// <summary>词缀名，例如 "Hale"。</summary>
    public string Affix { get; set; } = "";

    /// <summary>词缀组。</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// 各部位权重。这一版导出的数据里是空对象，保留原始结构以便以后接入命中率。
    /// 用 JsonElement 是为了不锁死形状（空对象 / 数字 / 嵌套对象都能读进来）。
    /// </summary>
    public JsonElement Weight { get; set; }

    /// <summary>「+(70-84) to maximum Life」这种括号区间。</summary>
    private static readonly Regex BracketRange = new(
        @"\(\s*(-?\d+(?:\.\d+)?)\s*-\s*(-?\d+(?:\.\d+)?)\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex Number = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);

    private bool rangeParsed;
    private double? rangeMin;
    private double? rangeMax;

    /// <summary>
    /// text 里的数值区间；判不出来为 null（界面按「无法判定等阶上限」退化，不崩）。
    ///
    /// 只在**同一条词缀自己这个 tier 内**重掷，所以这个区间就是重掷能到的范围。
    /// 能认的形态：
    ///   "+(70-84) to maximum Life" / "+(5-8)% increased ..." —— 一个括号区间；
    ///   "10% increased Movement Speed" —— 单值 tier，min = max。
    /// 两个区间的（"Adds (1-2) to (4-5) Physical Damage"）判不出哪个是主数值，返回 null。
    /// </summary>
    public (double Min, double Max)? Range
    {
        get
        {
            if (!rangeParsed)
            {
                rangeParsed = true;
                (rangeMin, rangeMax) = ParseRange(Text);
            }

            return rangeMin.HasValue && rangeMax.HasValue ? (rangeMin.Value, rangeMax.Value) : null;
        }
    }

    private static (double? Min, double? Max) ParseRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var brackets = BracketRange.Matches(text);
        if (brackets.Count > 1)
        {
            // 形如 "Adds (1-2) to (4-5)"：两段区间，主数值只可能来自物品行，
            // 从这里猜会给出错误的上限，宁可交给调用方标注「无法判定」。
            return (null, null);
        }

        if (brackets.Count == 1)
        {
            var group = brackets[0];
            return double.TryParse(group.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
                   && double.TryParse(group.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
                   && max >= min
                       ? (min, max)
                       : (null, null);
        }

        // 没有括号的单值 tier（例如移动速度 10 / 15 / 20 ...），取第一个数字。
        var single = Number.Match(text);
        return single.Success
               && double.TryParse(single.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                   ? (value, value)
                   : (null, null);
    }
}

/// <summary>
/// 词缀类型表的加载结果：要么拿到数据，要么带着「为什么没有」的原因，绝不静默失败。
/// </summary>
public class AffixTypeSnapshot
{
    public AffixTypeFile? Data { get; init; }

    public string? LoadedFrom { get; init; }

    public string? LoadError { get; init; }

    public IReadOnlyList<string> SearchedPaths { get; init; } = [];

    public bool HasData => Data != null;
}
