using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// C2b 的排序是纯函数，这里钉住四条约定（界面直接照它渲染，错了用户看到的就是错名次）：
///   1. 算出来的在前，按选定指标降序；
///   2. 同指标并列时看另一个指标（否则「DPS 一样、EHP 差很多」的两条看起来是随机顺序）；
///   3. **只公布前 N 名**（用户要的就是 top5）；
///   4. **没算出来的照样带出来**（带原因），不参与排名也不从结果里消失 ——
///      直接从表里消失，用户会以为「这条词缀不存在」或「它的收益是 0」。
/// </summary>
public class AffixGainTests
{
    private static AffixGainRow Row(string text, double dps, double ehp, bool isImplicit = false) => new()
    {
        Text = text,
        Implicit = isImplicit,
        Status = PobCompareStatus.Success,
        DpsDelta = dps,
        EhpDelta = ehp,
    };

    private static AffixGainRow Unranked(string text, PobCompareStatus status) => new()
    {
        Text = text,
        Status = status,
        Error = "boom",
    };

    [Fact]
    public void Ranks_by_the_selected_metric_and_only_publishes_the_top_n()
    {
        var rows = new[]
        {
            Row("+10 to maximum Life", 1_000, 100),
            Row("+20 to maximum Life", 3_000, 100),
            Row("+30 to maximum Life", 2_000, 900),
            Row("+40 to maximum Life", 500, 5_000),
            Row("+50 to maximum Life", 4_000, 10),
            Row("+60 to maximum Life", 2_500, 20),
        };

        var ranking = AffixGainRanking.Rank(rows, CandidateRankMetric.Dps, count: 5);

        Assert.Equal(5, ranking.Top.Count);
        Assert.Equal(["+50 to maximum Life", "+20 to maximum Life", "+60 to maximum Life", "+30 to maximum Life", "+10 to maximum Life"],
            ranking.Top.Select(x => x.Text));

        // 换指标只本地重排：EHP 最高的那条必须变成第一，而且样本一个不多一个不少
        var byEhp = AffixGainRanking.Rank(rows, CandidateRankMetric.Ehp, count: 5);
        Assert.Equal("+40 to maximum Life", byEhp.Top[0].Text);
        Assert.Equal(rows.Length, byEhp.Rows.Count);
    }

    [Fact]
    public void Ties_on_the_metric_are_broken_by_the_other_metric()
    {
        var rows = new[]
        {
            Row("worse ehp", 2_000, 100),
            Row("better ehp", 2_000, 400),
        };

        var ranking = AffixGainRanking.Rank(rows, CandidateRankMetric.Dps, count: 5);

        Assert.Equal(["better ehp", "worse ehp"], ranking.Top.Select(x => x.Text));
    }

    [Fact]
    public void Unranked_rows_are_reported_with_their_reason_and_never_ranked()
    {
        var rows = new[]
        {
            Unranked("engine died here", PobCompareStatus.EngineUnavailable),
            Row("+20 to maximum Life", 3_000, 100),
            Unranked("bad line", PobCompareStatus.Failed),
        };

        var ranking = AffixGainRanking.Rank(rows, CandidateRankMetric.Dps, count: 5);

        Assert.Single(ranking.Top);
        Assert.Equal("+20 to maximum Life", ranking.Top[0].Text);
        Assert.Equal(3, ranking.Rows.Count);
        Assert.Equal(2, ranking.NotRanked.Count);

        // 没算出来的行不是「0 收益」：状态要原样带出来，界面才解释得出「为什么没有数字」
        Assert.Equal(PobCompareStatus.EngineUnavailable, ranking.NotRanked[0].Status);
        Assert.Equal(PobCompareStatus.Failed, ranking.NotRanked[1].Status);
        Assert.False(ranking.NotRanked[0].IsRanked);
    }

    /// <summary>一条都没算出来时不许给出「前 5 名」的假象（Top 为空，界面走"没有数字"的分支）。</summary>
    [Fact]
    public void No_ranked_rows_means_no_top()
    {
        var rows = new[] { Unranked("a", PobCompareStatus.EngineUnavailable), Unranked("b", PobCompareStatus.Failed) };

        var ranking = AffixGainRanking.Rank(rows, CandidateRankMetric.Dps, count: 5);

        Assert.Empty(ranking.Top);
        Assert.Equal(2, ranking.NotRanked.Count);
    }
}
