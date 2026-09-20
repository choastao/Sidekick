using System.Globalization;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 备选篮「前提 / 可比性」的守门员（<see cref="BasketComparability"/>）。
///
/// 病根：备选篮里每一行都是**在某个模板 + 某个场景下试穿算出来的**；
/// 用户换了模板 / 场景之后旧数字还挂在界面上照样排名 —— 那是拿两个口径的数比大小。
/// 两条判据：
///   · 所有已算出的行必须**共享同一个前提**才给「最优 / 名次」；
///   · 与**当前**前提不一致的行判陈旧（数字保留，但要打标记并摘掉名次）。
/// </summary>
public class BasketComparabilityTests
{
    private static BasketPremise Premise(
        string? templateId = "tpl-a",
        string? context = PobContexts.Build,
        string? metricKey = PobPrimaryMetric.TotalDps,
        int generation = 1) => new(templateId, context, metricKey, generation);

    // ---- AreComparable ----

    [Fact]
    public void 同一个前提的行_可比()
    {
        Assert.True(BasketComparability.AreComparable([
            Premise(),
            Premise(),
            Premise(),
        ]));
    }

    [Fact]
    public void 没有任何行_或只有一行_算可比()
    {
        // 没有「不一致」可言 —— 不给名次不是因为不可比，而是因为没有行。
        Assert.True(BasketComparability.AreComparable([]));
        Assert.True(BasketComparability.AreComparable([Premise()]));
    }

    [Theory]
    [InlineData("tpl-b", PobContexts.Build, PobPrimaryMetric.TotalDps, 1)]           // 换了模板
    [InlineData("tpl-a", PobContexts.Boss, PobPrimaryMetric.TotalDps, 1)]            // 换了场景
    [InlineData("tpl-a", PobContexts.Build, PobPrimaryMetric.CombinedDps, 1)]        // 换了主指标
    [InlineData("tpl-a", PobContexts.Build, PobPrimaryMetric.TotalDps, 2)]           // 引擎重启过（代际变了）
    public void 任一前提不同_就不可比(string? templateId, string? context, string? metricKey, int generation)
    {
        Assert.False(BasketComparability.AreComparable([
            Premise(),
            Premise(templateId, context, metricKey, generation),
        ]));
    }

    [Fact]
    public void 场景大小写不同仍算同一个前提()
    {
        // 协议里都是大写，别因为大小写把一整批判成不可比
        Assert.True(BasketComparability.AreComparable([
            Premise(context: PobContexts.Map),
            Premise(context: "map"),
        ]));
    }

    [Fact]
    public void 不可比只要有一对不一致就成立_不必首尾成对()
    {
        Assert.False(BasketComparability.AreComparable([
            Premise(),
            Premise(),
            Premise(generation: 3),
            Premise(),
        ]));
    }

    // ---- IsStale ----

    [Fact]
    public void 前提一致的不算陈旧()
    {
        Assert.False(BasketComparability.IsStale(Premise(), Premise()));
    }

    [Theory]
    [InlineData("tpl-b", PobContexts.Build, PobPrimaryMetric.TotalDps, 1)]
    [InlineData("tpl-a", PobContexts.Map, PobPrimaryMetric.TotalDps, 1)]
    [InlineData("tpl-a", PobContexts.Build, PobPrimaryMetric.FullDps, 1)]
    [InlineData("tpl-a", PobContexts.Build, PobPrimaryMetric.TotalDps, 7)]
    public void 与当前前提不一致_就是陈旧(string? templateId, string? context, string? metricKey, int generation)
    {
        Assert.True(BasketComparability.IsStale(
            Premise(),
            Premise(templateId, context, metricKey, generation)));
    }

    [Fact]
    public void 当前前提里主指标为_null_与记录的那份不一致_判陈旧()
    {
        // 这是「当前拿不到主指标」的真实形态（没载入过基线 / 换了模板）：
        // 与记录的值一比就不同 → 判陈旧（要重排），**不许猜一个值出来**。
        Assert.True(BasketComparability.IsStale(
            Premise(),
            Premise(metricKey: null)));

        Assert.False(BasketComparability.IsStale(
            Premise(metricKey: null),
            Premise(metricKey: null)));
    }

    [Fact]
    public void 当前模板为_null_与记录的那份不一致_判陈旧()
    {
        Assert.True(BasketComparability.IsStale(Premise(), Premise(templateId: null)));
    }

    // ---- 资源键（中文 / 英文都要在） ----

    [Theory]
    [InlineData("Pob_Verdict_Label")]
    [InlineData("EngineVerdict_Unresolved")]
    [InlineData("EngineVerdict_NoChange")]
    [InlineData("EngineVerdict_Sidegrade")]
    [InlineData("EngineVerdict_OffenseUpgrade")]
    [InlineData("EngineVerdict_DefenseUpgrade")]
    [InlineData("EngineVerdict_ClearUpgrade")]
    [InlineData("EngineVerdict_StrongUpgrade")]
    [InlineData("EngineVerdict_Tradeoff")]
    [InlineData("EngineVerdict_Downgrade")]
    [InlineData("EngineVerdict_StrongDowngrade")]
    [InlineData("Verdict_Reason_HardGate")]
    [InlineData("Verdict_Reason_NoMetrics")]
    [InlineData("Verdict_Reason_ResCap")]
    [InlineData("Verdict_Reason_NoChange")]
    [InlineData("Verdict_Reason_BothUp")]
    [InlineData("Verdict_Reason_Offense")]
    [InlineData("Verdict_Reason_Defense")]
    [InlineData("Verdict_Reason_BothDown")]
    [InlineData("Verdict_Reason_Mixed")]
    [InlineData("Verdict_Reason_OnlyEhp")]
    [InlineData("Pareto_Label")]
    [InlineData("Pareto_Dominates")]
    [InlineData("Pareto_Tradeoff")]
    [InlineData("Pareto_Dominated")]
    [InlineData("Pareto_Equal")]
    [InlineData("Basket_Stale_Reason")]
    [InlineData("Basket_Not_Comparable")]
    public void 新增资源键中英两份都在(string key)
    {
        var zh = new TestLocalizer(CultureInfo.GetCultureInfo("zh"))[key];
        var en = new TestLocalizer(CultureInfo.GetCultureInfo("en"))[key];

        Assert.False(zh.ResourceNotFound, $"{key} 在 zh 里缺了");
        Assert.False(en.ResourceNotFound, $"{key} 在 en 里缺了");
        Assert.False(string.IsNullOrWhiteSpace(zh.Value), $"{key} 的 zh 文案是空的");
        Assert.False(string.IsNullOrWhiteSpace(en.Value), $"{key} 的 en 文案是空的");
    }

    [Fact]
    public void 判定枚举的每一档都有对应文案()
    {
        var zh = new TestLocalizer(CultureInfo.GetCultureInfo("zh"));

        foreach (var verdict in Enum.GetValues<ItemVerdict>())
        {
            Assert.False(zh[$"EngineVerdict_{verdict}"].ResourceNotFound, $"EngineVerdict_{verdict} 缺文案");
        }

        foreach (var pareto in Enum.GetValues<ParetoStatus>())
        {
            Assert.False(zh[$"Pareto_{pareto}"].ResourceNotFound, $"Pareto_{pareto} 缺文案");
        }
    }

    [Fact]
    public void 判定用的理由键都有对应文案()
    {
        var zh = new TestLocalizer(CultureInfo.GetCultureInfo("zh"));

        string[] keys =
        [
            ItemVerdictDecider.ReasonHardGate,
            ItemVerdictDecider.ReasonNoMetrics,
            ItemVerdictDecider.ReasonResCap,
            ItemVerdictDecider.ReasonNoChange,
            ItemVerdictDecider.ReasonBothUp,
            ItemVerdictDecider.ReasonOffense,
            ItemVerdictDecider.ReasonDefense,
            ItemVerdictDecider.ReasonBothDown,
            ItemVerdictDecider.ReasonMixed,
            ItemVerdictDecider.ReasonOnlyEhp,
            ItemVerdictDecider.ReasonOnlyDps,
        ];

        foreach (var key in keys)
        {
            Assert.False(zh[key].ResourceNotFound, $"{key} 缺文案");
        }
    }

    [Fact]
    public void 判定实际产出的理由键_都在资源里()
    {
        var zh = new TestLocalizer(CultureInfo.GetCultureInfo("zh"));

        // 扫一遍所有理由键的组合（两维的取值范围覆盖每条分支），把实际会显示出来的键收齐再核对。
        double?[] values = [null, -6, -5, -2, -1, -0.5, 0, 0.5, 1, 2, 5, 6];
        var produced = new HashSet<string>();

        foreach (var offense in values)
        {
            foreach (var defense in values)
            {
                foreach (var hardGate in new[] { false, true })
                {
                    foreach (var resCap in new[] { false, true })
                    {
                        var input = new ItemVerdictInput(
                            offense,
                            defense,
                            hardGate,
                            resCap,
                            MetricsPartial: offense is null);

                        produced.Add(ItemVerdictDecider.Decide(input).ReasonKey);
                    }
                }
            }
        }

        foreach (var key in produced)
        {
            Assert.False(zh[key].ResourceNotFound, $"{key} 是实际会显示出来的理由键，但资源里没有");
        }
    }
}
