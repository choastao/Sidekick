namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// Craft of Exile 的 PoE2 做装模型数据（affix-pool-coe.json）的根节点。
///
/// 这张表回答的是「在这个底材上，这一侧能抽出哪些 (词缀族, 档位) 对，各自的权重是多少」。
/// 与 affix-weights.json 的区别：那份数据的权重只有 0/1（只够回答「能不能出」），
/// 这份是真实权重 —— 概率必须按 <c>Σ命中权重 / Σ池内权重</c> 算，不能数条数。
///
/// 数据来源：Craft of Exile（craftofexile.com）的 PoE2 数据，官方不公布成功率，
/// 所以这里算出来的仍然是参考值，不是保证。
/// </summary>
public class AffixPoolCoeFile
{
    /// <summary>数据来源说明（界面照实展示）。</summary>
    public string Source { get; set; } = "";

    /// <summary>生成方的模型说明。</summary>
    public string Model { get; set; } = "";

    /// <summary>底材 id -&gt; 底材定义。</summary>
    public Dictionary<string, AffixPoolCoeBase> Bases { get; set; } = [];

    /// <summary>全部词缀（前缀 + 后缀），每条挂一个或多个底材的档位表。</summary>
    public List<AffixPoolCoeMod> Mods { get; set; } = [];
}

/// <summary>
/// 一个底材（不是我们的部位标签）：部位 + 属性组合。
/// 「我们的槽位 + 属性」到这张表里的底材 id 的映射见 <see cref="AffixPoolTags.ResolveBase"/>。
/// </summary>
public class AffixPoolCoeBase
{
    /// <summary>底材名，例如 "Boots (STR/DEX)"。</summary>
    public string Name { get; set; } = "";

    /// <summary>部位，例如 belt / boots / weapon1h / offhand。</summary>
    public string Slot { get; set; } = "";

    /// <summary>属性组合（str / dex / int 的子集，可以为空）。</summary>
    public List<string> Attrs { get; set; } = [];

    /// <summary>底材分组，例如 "Boots" / "Jewellery"。</summary>
    public string Group { get; set; } = "";
}

/// <summary>
/// 一条词缀（一个词缀族）在不同底材上的全部档位。
/// </summary>
public class AffixPoolCoeMod
{
    /// <summary>数据里的词缀 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>词缀族，例如 "FireResistance" / "ChaosResistance"。</summary>
    public string Family { get; set; } = "";

    /// <summary>该条挂的全部词缀族（多数只有一族，混合词缀挂多族）。</summary>
    public List<string> Families { get; set; } = [];

    /// <summary>原始类型字符串："Prefix" / "Suffix"。</summary>
    public string Type { get; set; } = "";

    /// <summary>词缀文本（含 "#" 占位符），例如 "+#% to Fire Resistance"。</summary>
    public string Text { get; set; } = "";

    /// <summary>底材 id -&gt; 该族在这个底材上的全部档位（含 ilvl、数值区间、权重）。</summary>
    public Dictionary<string, List<AffixPoolCoeTier>> Bases { get; set; } = [];

    /// <summary>解析成枚举；数据里出现别的写法按「未知」处理，不猜。</summary>
    public AffixSide Side => Type?.Trim().ToLowerInvariant() switch
    {
        "prefix" => AffixSide.Prefix,
        "suffix" => AffixSide.Suffix,
        _ => AffixSide.Unknown,
    };
}

/// <summary>一个 (词缀族, 档位) 对：ilvl 门槛、数值区间、权重。</summary>
public class AffixPoolCoeTier
{
    /// <summary>这个档位的 ilvl 门槛（池里只收 ilvl &lt;= 物品等级的）。</summary>
    public int Ilvl { get; set; }

    /// <summary>档位数值下限。数据里可以为 null（这条词缀的文本本身没有数值，例如「不能冰冻」）。</summary>
    public double? Min { get; set; }

    /// <summary>档位数值上限（判「数值够不够目标」用这个，不再去正则抠文本）。</summary>
    public double? Max { get; set; }

    /// <summary>权重（真实权重，例如常规词缀 1000、混沌抗 250）。</summary>
    public double W { get; set; }
}

/// <summary>
/// 池里的一条：某个 (词缀族, 档位) 对，带真实权重。
///
/// 这是 <see cref="Services.ExpectedCostCalculator.Estimate"/> 的入参单位 ——
/// 概率是「命中条目的权重之和 / 池内条目的权重之和」，所以必须带着 <see cref="Weight"/> 走。
/// </summary>
public sealed class AffixPoolEntry
{
    /// <summary>词缀族，例如 "FireResistance"。</summary>
    public string Family { get; init; } = "";

    /// <summary>词缀文本（含 "#" 占位符），例如 "+#% to Fire Resistance"。</summary>
    public string Text { get; init; } = "";

    /// <summary>前缀还是后缀。</summary>
    public AffixSide Side { get; init; }

    /// <summary>这个档位的 ilvl 门槛。</summary>
    public int ItemLevel { get; init; }

    /// <summary>档位数值下限；数据里没有数值是 null。</summary>
    public double? Min { get; init; }

    /// <summary>档位数值上限；数据里没有数值是 null（这时判「数值够不够」只能按「判不了」处理）。</summary>
    public double? Max { get; init; }

    /// <summary>真实权重。</summary>
    public double Weight { get; init; }

    private string? normalizedText;

    /// <summary>归一化后的词缀文本（缓存）：只保留字母并转小写，见 <see cref="ModWeight.Normalize"/>。</summary>
    public string NormalizedText => normalizedText ??= ModWeight.Normalize(Text);
}
