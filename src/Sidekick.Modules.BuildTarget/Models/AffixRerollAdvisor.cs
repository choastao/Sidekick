namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「这条词缀的数值缺口，神圣石重掷够不够得到？」的判定。
///
/// 神圣石只重置**当前这条词缀所属 tier 的数值区间**，官方与社区攻略一致：它不会造成等阶变化。
/// 所以目标一旦高过当前 tier 的上限，重掷到死也够不到，只能把这条词缀换掉（混沌石）。
/// </summary>
public static class AffixRerollAdvisor
{
    /// <summary>
    /// <paramref name="current"/> 是当前装备上这条词缀的取值，<paramref name="target"/> 是目标下限。
    /// <paramref name="preset"/> 为空（词缀类型表里没有这条词缀）时按「无法判定」退化。
    /// </summary>
    public static RerollAdvice Decide(AffixTypePreset? preset, double current, double target)
    {
        var hits = preset?.FindCoveringTiers(current) ?? [];
        if (hits.Count == 0)
        {
            // 数据缺失，或这条词缀的 tier 文本解析不出数值区间：标出来，别假装能重掷到。
            return new RerollAdvice(null, null, CanReroll: true, CapUnknown: true);
        }

        // 词缀类型表是多条梯子聚合的（同一条词缀在不同部位类别下各有一条：全局 / 本地等），
        // 同一个数值可能落在多条梯子上。取上限最低的那条当判据：宁可保守地建议换掉，
        // 也不给一个永远够不到的重掷建议——那正是「洗到死也达不到」的坑。
        var tier = hits.OrderBy(x => x.Max).First();

        var needed = preset!.TierLadder
                           .Where(x => x.Tier.Group == tier.Tier.Group && x.Max >= target)
                           .OrderBy(x => x.Number)
                           .FirstOrDefault();

        return new RerollAdvice(tier, needed, CanReroll: target <= tier.Max, CapUnknown: false);
    }
}

/// <summary>
/// 一条词缀数值缺口的判定结果。
/// </summary>
/// <param name="Tier">当前值所属的 tier（重掷能到的上限 = 它的 Max）；判不出来为 null。</param>
/// <param name="NeededTier">够到目标所需的最低等阶（同一条梯子里找）；这条梯子没有能达到目标的等阶时为 null。</param>
/// <param name="CanReroll">true = 重掷数值（神圣石）够得到目标；false = 只能换掉这条词缀。</param>
/// <param name="CapUnknown">true = 数据缺失 / 数值区间解析不出来，重掷建议上要标注「无法判定等阶上限」。</param>
public sealed record RerollAdvice(AffixTierHit? Tier, AffixTierHit? NeededTier, bool CanReroll, bool CapUnknown);
