using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 主指标回退链的守门员。
///
/// 为什么要钉住它：
///   1. **默认路径绝不能变** —— TotalDPS 仍是主指标（我们的基准数字与真机对账都建立在它上面），
///      回退只是在引擎给不出这个数时的补救，不是换口径；
///   2. **算不出来时不许退化成 0** —— 全为 0 必须回「没有数字」，界面才会说「算不出来」
///      而不是显示一个 0（0 会被读成「这件装备没变化」）；
///   3. **选谁一律按基线那份 stats** —— 一处按基线、一处按候选去选，会让两个不同口径的数相减。
/// </summary>
public class PobPrimaryMetricTests
{
    private static PobStats Stats(double dps, double combinedDps = 0, double fullDps = 0, double ehp = 1, double life = 1) =>
        new(dps, ehp, life) { CombinedDps = combinedDps, FullDps = fullDps };

    [Fact]
    public void 默认走_TotalDPS()
    {
        var (key, value, resolved) = PobPrimaryMetric.Select(Stats(7_644_241, combinedDps: 7_649_175));

        Assert.Equal(PobPrimaryMetric.TotalDps, key);
        Assert.Equal(7_644_241, value);
        Assert.True(resolved);
    }

    [Fact]
    public void TotalDPS_没有时按顺序回退到_Combined_再_Full()
    {
        var combined = PobPrimaryMetric.Select(Stats(0, combinedDps: 7_649_175, fullDps: 9_000_000));
        Assert.Equal(PobPrimaryMetric.CombinedDps, combined.Key);
        Assert.Equal(7_649_175, combined.Value);
        Assert.True(combined.Resolved);

        var full = PobPrimaryMetric.Select(Stats(0, combinedDps: 0, fullDps: 9_000_000));
        Assert.Equal(PobPrimaryMetric.FullDps, full.Key);
        Assert.Equal(9_000_000, full.Value);
        Assert.True(full.Resolved);
    }

    /// <summary>负值一律当「没有」：拿负数当基准会让「涨了多少」的符号整个反过来。</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(-1, -1, -1)]
    public void 全为_0_或负值时算不出来(double dps, double combined, double full)
    {
        var (key, value, resolved) = PobPrimaryMetric.Select(Stats(dps, combined, full));

        Assert.Equal(string.Empty, key);
        Assert.Equal(0, value);
        Assert.False(resolved);
    }

    [Fact]
    public void DisplayName_给出三个指标的界面名_其余为空()
    {
        Assert.Equal("主技能 DPS", PobPrimaryMetric.DisplayName(PobPrimaryMetric.TotalDps));
        Assert.Equal("综合 DPS", PobPrimaryMetric.DisplayName(PobPrimaryMetric.CombinedDps));
        Assert.Equal("全技能 DPS", PobPrimaryMetric.DisplayName(PobPrimaryMetric.FullDps));

        // 认不出的键（含空串）回空串：界面据此不显示那一行，而不是显示一个空括号
        Assert.Equal(string.Empty, PobPrimaryMetric.DisplayName(""));
        Assert.Equal(string.Empty, PobPrimaryMetric.DisplayName(null));
        Assert.Equal(string.Empty, PobPrimaryMetric.DisplayName("SomethingElse"));
    }

    [Fact]
    public void 对比结果按基线选指标()
    {
        // 基线算不出 TotalDPS、回退到 CombinedDPS；候选那份的 TotalDPS 有数 ——
        // 这时**不许**改用候选的 TotalDPS（那等于拿两个口径的数相减）。
        var result = PobCompareResult.Ok(
            Stats(0, combinedDps: 1_000),
            Stats(50_000, combinedDps: 60_000),
            unmappedAffixes: 0);

        Assert.Equal(PobPrimaryMetric.CombinedDps, result.PrimaryMetricKey);
        Assert.False(result.DpsUnavailable);
    }

    [Fact]
    public void 基线算不出伤害时标记为不可用()
    {
        var result = PobCompareResult.Ok(Stats(0, 0, 0, ehp: 23_554), Stats(0, 0, 0, ehp: 24_711), unmappedAffixes: 0);

        Assert.Equal(string.Empty, result.PrimaryMetricKey);
        Assert.True(result.DpsUnavailable);
        Assert.True(result.HasDelta);   // EHP 还是能对比的
    }

    [Fact]
    public void 逐条收益那条路径带同一口径()
    {
        var measure = PobMeasure.Succeeded(Stats(0, fullDps: 4_000), Stats(0, fullDps: 4_500));
        Assert.Equal(PobPrimaryMetric.FullDps, measure.PrimaryMetricKey);
        Assert.False(measure.DpsUnavailable);

        var unavailable = PobMeasure.Succeeded(Stats(0, 0, 0), Stats(0, 0, 0));
        Assert.Equal(string.Empty, unavailable.PrimaryMetricKey);
        Assert.True(unavailable.DpsUnavailable);
    }

    /// <summary>DPS 算不出来时，逐条词缀收益改按 EHP 排名（否则排的是一列 0）。</summary>
    [Theory]
    [InlineData(true, CandidateRankMetric.Dps, CandidateRankMetric.Ehp)]
    [InlineData(true, CandidateRankMetric.Ehp, CandidateRankMetric.Ehp)]
    [InlineData(false, CandidateRankMetric.Dps, CandidateRankMetric.Dps)]
    [InlineData(false, CandidateRankMetric.Ehp, CandidateRankMetric.Ehp)]
    public void 算不出伤害时逐条收益按_EHP_排名(bool unavailable, CandidateRankMetric requested, CandidateRankMetric expected)
    {
        Assert.Equal(expected, PobAffixGainService.EffectiveMetric(unavailable, requested));
    }
}
