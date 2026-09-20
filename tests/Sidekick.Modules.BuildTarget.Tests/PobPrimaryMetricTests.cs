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

    /// <summary>
    /// 按 key 取值是**唯一那套 switch**（B1 的接线就靠它）：三个键各取各的字段，
    /// 认不出的键回 0（调用方按 Resolved / DpsUnavailable 判有没有数，不许去比这个 0）。
    /// </summary>
    [Fact]
    public void 按_key_取值各取各的字段()
    {
        var stats = Stats(dps: 11, combinedDps: 22, fullDps: 33);

        Assert.Equal(11, PobPrimaryMetric.Value(stats, PobPrimaryMetric.TotalDps));
        Assert.Equal(22, PobPrimaryMetric.Value(stats, PobPrimaryMetric.CombinedDps));
        Assert.Equal(33, PobPrimaryMetric.Value(stats, PobPrimaryMetric.FullDps));

        Assert.Equal(0, PobPrimaryMetric.Value(stats, ""));
        Assert.Equal(0, PobPrimaryMetric.Value(stats, null));
        Assert.Equal(0, PobPrimaryMetric.Value(stats, "SomethingElse"));
    }

    /// <summary>
    /// <see cref="PobPrimaryMetric.Select"/> 与 <see cref="PobPrimaryMetric.Value"/> 必须对同一份 stats 说同一件事：
    /// 选出来的那个 key 取出来的数**就是** Select 回的那个 Value。两处若各写一套 switch，这个等式先破。
    /// </summary>
    [Theory]
    [InlineData(100, 0, 0)]
    [InlineData(0, 200, 0)]
    [InlineData(0, 0, 300)]
    [InlineData(100, 200, 300)]
    public void Select_与_Value_是同一个口径(double dps, double combined, double full)
    {
        var stats = Stats(dps, combined, full);
        var (key, value, resolved) = PobPrimaryMetric.Select(stats);

        Assert.True(resolved);
        Assert.Equal(value, PobPrimaryMetric.Value(stats, key));
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

    // ---- B1 回归（这一组是「标签换了、数字没换」那个洞的钉子）----
    //
    // 病根：差值写的是 `Current.Dps - Base.Dps`（也就是 TotalDPS），而标签写的是回退链选出来的那个指标。
    // 回退档（TotalDPS = 0、CombinedDPS 有数）下面板显示 0 / 0 / +0，同一区块却写着「本次用的伤害指标：综合 DPS」，
    // 判定还会落到「引擎算不出伤害」—— 与上一行自相矛盾。原来的单测只断言了 Key / DpsUnavailable，照不到数字。

    [Fact]
    public void 回退档下差值与百分比用的是所选主指标的数字()
    {
        var result = PobCompareResult.Ok(
            Stats(0, combinedDps: 100),        // TotalDPS = 0 → 回退到 CombinedDPS
            Stats(0, combinedDps: 120),
            unmappedAffixes: 0);

        Assert.Equal(PobPrimaryMetric.CombinedDps, result.PrimaryMetricKey);
        Assert.False(result.DpsUnavailable);

        // 这三个数就是本批要修的：改之前是 0 / 0 / null
        Assert.Equal(100, result.BaseMetricValue);
        Assert.Equal(120, result.CurrentMetricValue);
        Assert.Equal(20, result.DpsDelta);
        Assert.Equal(20, result.DpsPercent!.Value, 9);
    }

    [Fact]
    public void 回退档下候选更低时出差值()
    {
        var result = PobCompareResult.Ok(Stats(0, combinedDps: 100), Stats(0, combinedDps: 80), unmappedAffixes: 0);

        Assert.Equal(PobPrimaryMetric.CombinedDps, result.PrimaryMetricKey);
        Assert.Equal(-20, result.DpsDelta);
        Assert.Equal(-20, result.DpsPercent!.Value, 9);
    }

    [Fact]
    public void 回退档下回退到_FullDPS_时也是同一套数字()
    {
        // CombinedDPS 也没有 → 再退一档到 FullDPS，取值必须跟着那个 key 走
        var result = PobCompareResult.Ok(
            Stats(0, combinedDps: 0, fullDps: 4_000),
            Stats(0, combinedDps: 0, fullDps: 4_500),
            unmappedAffixes: 0);

        Assert.Equal(PobPrimaryMetric.FullDps, result.PrimaryMetricKey);
        Assert.Equal(4_000, result.BaseMetricValue);
        Assert.Equal(4_500, result.CurrentMetricValue);
        Assert.Equal(500, result.DpsDelta);
        Assert.Equal(12.5, result.DpsPercent!.Value, 9);
    }

    /// <summary>
    /// 默认档（TotalDPS &gt; 0）**逐位不许变**：主指标还是 TotalDPS，差值与百分比与改动前完全一致
    /// （候选的 CombinedDPS 与基线的不同也不行 —— 那样等于拿两个口径的数相减）。
    /// </summary>
    [Fact]
    public void 默认档的差值与百分比与改动前一致()
    {
        var result = PobCompareResult.Ok(
            Stats(1_000, combinedDps: 9_000),        // 基线 TotalDPS 有数 → 主指标就是 TotalDPS
            Stats(1_050, combinedDps: 900),          // 候选的 Combined 更低，**不许**参与
            unmappedAffixes: 0);

        Assert.Equal(PobPrimaryMetric.TotalDps, result.PrimaryMetricKey);
        Assert.False(result.DpsUnavailable);
        Assert.Equal(1_000, result.BaseMetricValue);
        Assert.Equal(1_050, result.CurrentMetricValue);
        Assert.Equal(50, result.DpsDelta);           // = Current.Dps − Base.Dps（老行为）
        Assert.Equal(5, result.DpsPercent!.Value, 9); // = 50 / 1000 × 100
    }

    /// <summary>三个字段全为 0 时「算不出来」的语义不变：差值是 0，但不可用标志为真，界面显示「—」。</summary>
    [Fact]
    public void 一个指标都选不到时差值仍是_0_但标成不可用()
    {
        var result = PobCompareResult.Ok(Stats(0, 0, 0), Stats(0, 0, 0), unmappedAffixes: 0);

        Assert.Equal(string.Empty, result.PrimaryMetricKey);
        Assert.True(result.DpsUnavailable);
        Assert.Equal(0, result.BaseMetricValue);
        Assert.Equal(0, result.CurrentMetricValue);
        Assert.Equal(0, result.DpsDelta);
        Assert.Null(result.DpsPercent);
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
