using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 「期望成本」的计算：平均要操作几次、花多少钱才出目标。
///
///   期望次数 = 1 / P(该侧出一条满足目标数值的词缀)
///   期望成本 = 期望次数 × 单次操作成本
///
/// 两件事必须记住：
///   1. 权重来自 PoB-PoE2 的重组器实验 + 交易站统计（**社区逆向估算，官方不公布成功率**），
///      这里算的是参考值，不是保证；
///   2. 期望次数是**均值**。平均 1/10 次能出 ≠ 10 次必出。
///
/// 这个类不做任何 IO，也不碰价格：池由 <see cref="AffixWeightService"/> 给，
/// 单次操作成本由调用方（面板）按现有口径算好传进来。
/// </summary>
public static class ExpectedCostCalculator
{
    /// <summary>
    /// 改造 / 洗装（崇高石 / 混沌石）：从池里随机出一条词缀，求「出目标」的概率。
    ///
    /// 目标集合 = 池内满足「词缀文本命中目标模式」且「数值上限 ≥ 用户目标值」的条目。
    ///
    /// 为什么按**文本模式**而不是「group 完全相同」：同一条目标词缀在权重表里常常分属多个 group——
    /// 例如「最大生命」既有 IncreasedLife，也有局部混合词缀 LocalIncreasedEvasionAndLife
    /// （「+(7-10) to maximum Life」这类）；只认一个 group 会把池里的目标漏掉一大半，
    /// 概率算小、期望成本算大。按文本匹配才和实际能出生命的词缀条数一致。
    /// </summary>
    /// <param name="pool">该侧在某物品等级下的池。</param>
    /// <param name="patterns">
    /// 目标词缀的匹配模式（词缀库里的整行文本 / 预设关键词，中英均可）。
    /// 归一化后只比字母，所以「Adds # to # Fire Damage」能对上「Adds (5-8) to (11-14) Fire Damage」。
    /// </param>
    /// <param name="targetValue">用户要求的数值下限；&lt;= 0 表示只要求出这条词缀、不限数值。</param>
    /// <param name="singleOperationCost">单次操作成本（神聖石计价）。</param>
    public static ExpectedCostEstimate Estimate(
        IReadOnlyList<ModWeight>? pool,
        IEnumerable<string>? patterns,
        double targetValue,
        double singleOperationCost)
    {
        var mods = pool ?? [];
        var normalized = NormalizePatterns(patterns);

        var targetCount = 0;
        if (mods.Count > 0 && normalized.Count > 0)
        {
            foreach (var mod in mods)
            {
                if (Matches(mod, normalized) && SatisfiesValue(mod, targetValue))
                {
                    targetCount++;
                }
            }
        }

        return new ExpectedCostEstimate
        {
            PoolCount = mods.Count,
            TargetCount = targetCount,
            Probability = mods.Count > 0 ? (double)targetCount / mods.Count : 0,
            SingleOperationCost = singleOperationCost,
        };
    }

    /// <summary>
    /// 重掷数值（神圣石）：只在**当前这条词缀所属 tier 的数值区间内**重掷，分布按区间均匀，
    ///
    ///   P = (上限 - 目标值 + 1) / (上限 - 下限 + 1)
    ///
    /// 目标超过当前 tier 上限的情况不在这里（那种只能换掉词缀），调用方已经分流到「替换」。
    /// </summary>
    public static ExpectedCostEstimate EstimateReroll(
        double tierMin,
        double tierMax,
        double targetValue,
        double singleOperationCost)
    {
        if (!double.IsFinite(tierMin) || !double.IsFinite(tierMax) || tierMax < tierMin)
        {
            return new ExpectedCostEstimate { SingleOperationCost = singleOperationCost };
        }

        var span = tierMax - tierMin + 1d;
        if (span <= 0)
        {
            return new ExpectedCostEstimate { SingleOperationCost = singleOperationCost };
        }

        var hits = Math.Clamp(tierMax - targetValue + 1d, 0d, span);

        return new ExpectedCostEstimate
        {
            PoolCount = (int)Math.Round(span, MidpointRounding.AwayFromZero),
            TargetCount = (int)Math.Round(hits, MidpointRounding.AwayFromZero),
            Probability = hits / span,
            SingleOperationCost = singleOperationCost,
        };
    }

    /// <summary>池里有没有任何一条能命中目标模式（用于区分「池空」和「目标对不上」两种情况）。</summary>
    public static int CountPatternHits(IReadOnlyList<ModWeight>? pool, IEnumerable<string>? patterns)
    {
        var mods = pool ?? [];
        var normalized = NormalizePatterns(patterns);
        if (mods.Count == 0 || normalized.Count == 0)
        {
            return 0;
        }

        var hits = 0;
        foreach (var mod in mods)
        {
            if (Matches(mod, normalized))
            {
                hits++;
            }
        }

        return hits;
    }

    /// <summary>去掉空模式、去重、全部归一化。</summary>
    private static List<string> NormalizePatterns(IEnumerable<string>? patterns)
    {
        var normalized = new List<string>();
        if (patterns is null)
        {
            return normalized;
        }

        foreach (var pattern in patterns)
        {
            var text = ModWeight.Normalize(pattern);
            if (text.Length > 0 && !normalized.Contains(text))
            {
                normalized.Add(text);
            }
        }

        return normalized;
    }

    private static bool Matches(ModWeight mod, List<string> normalizedPatterns)
    {
        var text = mod.NormalizedText;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var pattern in normalizedPatterns)
        {
            if (text.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这条词缀的数值够不够用户目标。目标 &lt;= 0（只要出词缀）时不做数值判定。
    /// 数值上限解析不出来（文本里没有数字）的条目按「判不了」排除——宁可把成本估高，也别估低。
    /// </summary>
    private static bool SatisfiesValue(ModWeight mod, double targetValue)
    {
        if (targetValue <= 0)
        {
            return true;
        }

        return mod.UpperBound is { } upper && upper >= targetValue;
    }
}
