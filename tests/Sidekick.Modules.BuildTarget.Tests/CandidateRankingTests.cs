using Microsoft.Extensions.Logging.Abstractions;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// C2a：候选排序（引擎逐个试穿 → 按 DPS / EHP 增减排名）。
///
/// 这里覆盖**纯逻辑**（排序规则、行状态、缺原文的旧条目怎么处理）；
/// 「真的把装备挂进引擎」那一段由真机冒烟覆盖（PobCompareService 需要真引擎进程）。
/// </summary>
public class CandidateRankingTests
{
    // ---- 排序规则 ----

    [Fact]
    public void Sort_orders_ranked_rows_by_the_chosen_metric()
    {
        var rows = new[]
        {
            Row("a", dps: 100, ehp: -50),
            Row("b", dps: 300, ehp: 10),
            Row("c", dps: -20, ehp: 900),
        };

        var byDps = CandidateRanking.Sort(rows, CandidateRankMetric.Dps);
        Assert.Equal(["b", "a", "c"], byDps.Rows.Select(x => x.Id));

        var byEhp = CandidateRanking.Sort(rows, CandidateRankMetric.Ehp);
        Assert.Equal(["c", "b", "a"], byEhp.Rows.Select(x => x.Id));

        Assert.Equal("b", byDps.Best!.Id);
        Assert.Equal("c", byEhp.Best!.Id);
    }

    [Fact]
    public void Ties_fall_back_to_the_other_metric_then_input_order()
    {
        // 三行 DPS 增减完全相同 → 按 EHP 排；EHP 也相同的两行保持输入顺序（稳定排序）
        var rows = new[]
        {
            Row("low-ehp", dps: 50, ehp: 1),
            Row("tie-first", dps: 50, ehp: 7),
            Row("tie-second", dps: 50, ehp: 7),
        };

        var sorted = CandidateRanking.Sort(rows, CandidateRankMetric.Dps);

        Assert.Equal(["tie-first", "tie-second", "low-ehp"], sorted.Rows.Select(x => x.Id));
    }

    [Fact]
    public void Unranked_rows_are_listed_last_in_their_input_order()
    {
        var rows = new[]
        {
            Unranked("failed-1", PobCompareStatus.EngineUnavailable),
            Row("ok", dps: 10, ehp: 10),
            Unranked("no-text", PobCompareStatus.MissingText),
        };

        var sorted = CandidateRanking.Sort(rows, CandidateRankMetric.Dps);

        // 算出来的在前，其余按原顺序殿后 —— 一行都不许消失
        Assert.Equal(["ok", "failed-1", "no-text"], sorted.Rows.Select(x => x.Id));
        Assert.Equal(3, sorted.Rows.Count);
        Assert.Single(sorted.Ranked);
        Assert.Equal(2, sorted.NotRanked.Count);
    }

    [Fact]
    public void Switching_metric_keeps_every_row()
    {
        var rows = new[]
        {
            Row("a", dps: 1, ehp: 100),
            Row("b", dps: 100, ehp: 1),
            Unranked("c", PobCompareStatus.BaseUnknown),
        };

        var byDps = CandidateRanking.Sort(rows, CandidateRankMetric.Dps);
        var byEhp = CandidateRanking.Sort(byDps.Rows, CandidateRankMetric.Ehp);

        Assert.Equal(CandidateRankMetric.Ehp, byEhp.Metric);
        Assert.Equal(3, byEhp.Rows.Count);
        Assert.Equal("a", byEhp.Best!.Id);
    }

    // ---- 行状态 ----

    [Fact]
    public void Only_success_rows_carry_numbers()
    {
        var ranked = Row("a", dps: 10, ehp: 20);
        Assert.True(ranked.IsRanked);
        Assert.Equal(10, ranked.Value(CandidateRankMetric.Dps));
        Assert.Equal(20, ranked.Value(CandidateRankMetric.Ehp));

        // 没算出来的行即使带着数值字段也不参与排序（界面按 IsRanked 决定显不显示数字）
        var failed = Unranked("b", PobCompareStatus.Failed);
        Assert.False(failed.IsRanked);
    }

    // ---- 排序器：旧条目没有物品原文 ----

    [Fact]
    public async Task Candidates_without_item_text_are_reported_not_dropped()
    {
        // 旧版（v3.7 之前）入篮的条目只存了算好的贡献值，没有物品原文 → 没法拿去试穿。
        // ⚠ 空 Text 路径不会碰到 compare / parser，所以这里传 null 是安全的（真机路径由冒烟覆盖）。
        var ranker = new PobCandidateRanker(
            compare: null!,
            itemParser: null!,
            NullLogger<PobCandidateRanker>.Instance);

        var candidates = new List<CandidateBasketItem>
        {
            new() { Id = "old-1", Name = "旧戒指", SlotKey = SlotKeys.Ring1, Text = "" },
            new() { Id = "old-2", Name = "旧项链", SlotKey = SlotKeys.Amulet, Text = "   " },
        };

        var ranking = await ranker.RankAsync(template: null, candidates);

        Assert.Equal(2, ranking.Rows.Count);                     // 一行都不少
        Assert.Empty(ranking.Ranked);                            // 但一个名次都没有
        Assert.All(ranking.Rows, x => Assert.Equal(PobCompareStatus.MissingText, x.Status));
        Assert.All(ranking.Rows, x => Assert.Null(x.Error));      // 状态本身就说清了原因，不带错误文本
    }

    // ---- 脚手架 ----

    private static CandidateRankRow Row(string id, double dps, double ehp) => new()
    {
        Id = id,
        Name = id,
        SlotKey = SlotKeys.Helmet,
        Status = PobCompareStatus.Success,
        DpsDelta = dps,
        EhpDelta = ehp,
    };

    private static CandidateRankRow Unranked(string id, PobCompareStatus status) => new()
    {
        Id = id,
        Name = id,
        SlotKey = SlotKeys.Helmet,
        Status = status,
    };
}
