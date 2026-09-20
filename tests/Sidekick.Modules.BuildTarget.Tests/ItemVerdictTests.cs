using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 引擎指标判定的守门员（<see cref="ItemVerdictDecider"/>）。
///
/// 这一组钉三件事：
///   1. **顺序即优先级** —— 硬门槛 / 无指标 / 抗性守门必须压过数值上的「双升」；
///   2. **阈值** —— ±1% / ±5% 的上下沿（正好 +1% 不是「没变化」、正好 +5% 已经算「大幅」）；
///   3. **null ≠ 0** —— 算不出来的那一维不许参与比较，也不许凑成「没变化」。
/// </summary>
public class ItemVerdictTests
{
    private static ItemVerdictInput Input(
        double? offense,
        double? defense,
        bool hardGate = false,
        bool resCap = false,
        bool partial = false) =>
        new(offense, defense, hardGate, resCap, partial);

    private static (ItemVerdict Verdict, string ReasonKey) Decide(
        double? offense,
        double? defense,
        bool hardGate = false,
        bool resCap = false,
        bool partial = false) =>
        ItemVerdictDecider.Decide(Input(offense, defense, hardGate, resCap, partial));

    // ---- ① 硬性门槛（用户自己勾的）压过一切 ----

    [Fact]
    public void 硬门槛没过_即使攻防双升也是_有得有失()
    {
        var (verdict, reason) = Decide(offense: 40, defense: 40, hardGate: true);

        Assert.Equal(ItemVerdict.Tradeoff, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonHardGate, reason);
    }

    [Fact]
    public void 硬门槛没过_两维都没数时也走门槛这一条()
    {
        // 顺序：① 硬门槛 在 ② 无指标 之前
        var (verdict, reason) = Decide(offense: null, defense: null, hardGate: true);

        Assert.Equal(ItemVerdict.Tradeoff, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonHardGate, reason);
    }

    // ---- ② 两维都拿不到数 ----

    [Fact]
    public void 两维都没数_信息不足()
    {
        var (verdict, reason) = Decide(offense: null, defense: null);

        Assert.Equal(ItemVerdict.Unresolved, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonNoMetrics, reason);
    }

    [Fact]
    public void 两维都没数_即使标了_MetricsPartial_也还是信息不足()
    {
        // 顺序：② 无指标 在 ⑨ MetricsPartial 之前
        var (verdict, reason) = Decide(offense: null, defense: null, partial: true);

        Assert.Equal(ItemVerdict.Unresolved, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonNoMetrics, reason);
    }

    // ---- ③ 抗性上限守门 ----

    [Fact]
    public void 顶到上限的抗性被拉低_压过攻防双升()
    {
        var (verdict, reason) = Decide(offense: 30, defense: 30, resCap: true);

        Assert.Equal(ItemVerdict.Tradeoff, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonResCap, reason);
    }

    [Fact]
    public void 抗性守门_压过_基本没变化()
    {
        // 顺序：③ 抗性守门 在 ④ 没变化 之前 —— 幅度再小，缺口变糟就是变糟
        var (verdict, reason) = Decide(offense: 0.1, defense: 0.1, resCap: true);

        Assert.Equal(ItemVerdict.Tradeoff, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonResCap, reason);
    }

    // ---- ④ 基本没变化：两维都在 ±1% 内 ----

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0.9, -0.9)]
    [InlineData(-0.99, 0.99)]
    public void 两维都在正负1以内_基本没变化(double offense, double defense)
    {
        var (verdict, reason) = Decide(offense, defense);

        Assert.Equal(ItemVerdict.NoChange, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonNoChange, reason);
    }

    [Fact]
    public void 正好正负1_不算没变化_而算提升或下降那一侧()
    {
        // 边界：正好 +1% 归「提升」，正好 -1% 归「下降」（不落在「没变化」里）
        Assert.Equal(ItemVerdict.ClearUpgrade, Decide(1.0, 1.0).Verdict);
        Assert.Equal(ItemVerdict.Downgrade, Decide(-1.0, -1.0).Verdict);
    }

    // ---- ⑤ 进攻提升、防御没降 ----

    [Fact]
    public void 进攻提升防御持平_进攻提升()
    {
        var (verdict, reason) = Decide(offense: 3, defense: 0);

        Assert.Equal(ItemVerdict.OffenseUpgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonOffense, reason);
    }

    [Fact]
    public void 进攻提升防御小幅下降_仍算进攻提升()
    {
        // def > -1 允许防御小幅波动
        var (verdict, reason) = Decide(offense: 5, defense: -0.9);

        Assert.Equal(ItemVerdict.OffenseUpgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonOffense, reason);
    }

    [Fact]
    public void 攻防双升_明确提升()
    {
        var (verdict, reason) = Decide(offense: 2, defense: 2);

        Assert.Equal(ItemVerdict.ClearUpgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonBothUp, reason);
    }

    [Fact]
    public void 攻防双升且都到_5_才算大幅提升()
    {
        // 边界：正好 ±5% 归「大幅」那一侧
        Assert.Equal(ItemVerdict.ClearUpgrade, Decide(4.99, 5).Verdict);
        Assert.Equal(ItemVerdict.StrongUpgrade, Decide(5, 5).Verdict);
        Assert.Equal(ItemVerdict.StrongUpgrade, Decide(9, 7).Verdict);
    }

    [Fact]
    public void 一维不到_5_就只是明确提升_不是大幅()
    {
        var (verdict, reason) = Decide(offense: 12, defense: 4.9);

        Assert.Equal(ItemVerdict.ClearUpgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonBothUp, reason);
    }

    // ---- ⑥ 防御提升、进攻没降 ----

    [Fact]
    public void 防御提升进攻持平_防御提升()
    {
        var (verdict, reason) = Decide(offense: 0, defense: 3);

        Assert.Equal(ItemVerdict.DefenseUpgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonDefense, reason);
    }

    [Fact]
    public void 防御提升进攻小幅下降_仍算防御提升()
    {
        var (verdict, reason) = Decide(offense: -0.9, defense: 4);

        Assert.Equal(ItemVerdict.DefenseUpgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonDefense, reason);
    }

    // ---- ⑦ 攻防双降 ----

    [Fact]
    public void 攻防双降_下降()
    {
        var (verdict, reason) = Decide(offense: -2, defense: -3);

        Assert.Equal(ItemVerdict.Downgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonBothDown, reason);
    }

    [Fact]
    public void 攻防双降且都到_负5_才算大幅下降()
    {
        Assert.Equal(ItemVerdict.Downgrade, Decide(-4.99, -5).Verdict);
        Assert.Equal(ItemVerdict.StrongDowngrade, Decide(-5, -5).Verdict);
        Assert.Equal(ItemVerdict.StrongDowngrade, Decide(-30, -6).Verdict);
    }

    // ---- ⑧ 其余（一升一降） ----

    [Theory]
    [InlineData(3d, -3d)]
    [InlineData(-3d, 3d)]
    [InlineData(3d, -1d)]
    public void 一升一降_有得有失(double offense, double defense)
    {
        var (verdict, reason) = Decide(offense, defense);

        Assert.Equal(ItemVerdict.Tradeoff, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonMixed, reason);
    }

    [Fact]
    public void 只有进攻下降_防御没动_判下降()
    {
        // off=-2 / def=0：**只有一维下降**，另一维在容差内 → 下降（ReasonOffenseDown）。
        // 原来它落到 ⑧、被说成「一个升一个降」（文案说反了），这次改掉。
        var (verdict, reason) = Decide(offense: -2, defense: 0);

        Assert.Equal(ItemVerdict.Downgrade, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonOffenseDown, reason);

        // 镜像：只有防御下降。
        var mirror = Decide(offense: 0, defense: -2);
        Assert.Equal(ItemVerdict.Downgrade, mirror.Verdict);
        Assert.Equal(ItemVerdictDecider.ReasonDefenseDown, mirror.ReasonKey);

        // ⚠ 但**真·一升一降**（+3 / -1）必须仍是有得有失 —— ⑦b/⑦c 的容差判据不许把它吃掉。
        var mixed = Decide(offense: 3, defense: -1);
        Assert.Equal(ItemVerdict.Tradeoff, mixed.Verdict);
        Assert.Equal(ItemVerdictDecider.ReasonMixed, mixed.ReasonKey);
    }

    // ---- ⑨ 只有一维有数 ----

    [Theory]
    [InlineData(2d, ItemVerdict.DefenseUpgrade)]
    [InlineData(-2d, ItemVerdict.Downgrade)]
    [InlineData(0.5, ItemVerdict.NoChange)]
    public void 只有EHP有数_按EHP那一维定(double defense, ItemVerdict expected)
    {
        var (verdict, reason) = Decide(offense: null, defense, partial: true);

        Assert.Equal(expected, verdict);
        Assert.Equal(ItemVerdictDecider.ReasonOnlyEhp, reason);
    }

    [Fact]
    public void 只有EHP有数_阈值上下沿()
    {
        Assert.Equal(ItemVerdict.DefenseUpgrade, Decide(null, 1, partial: true).Verdict);
        Assert.Equal(ItemVerdict.NoChange, Decide(null, 0.99, partial: true).Verdict);
        Assert.Equal(ItemVerdict.NoChange, Decide(null, -0.99, partial: true).Verdict);
        Assert.Equal(ItemVerdict.Downgrade, Decide(null, -1, partial: true).Verdict);
    }

    [Fact]
    public void 只有DPS有数_镜像处理()
    {
        // 规格只写了「只有 EHP」那一半；另一半（EHP 算不出来）规格没定义，
        // 这里按镜像处理并给一条对称的理由键（见报告）。
        Assert.Equal(ItemVerdict.OffenseUpgrade, Decide(offense: 2, defense: null).Verdict);
        Assert.Equal(ItemVerdict.Downgrade, Decide(offense: -2, defense: null).Verdict);
        Assert.Equal(ItemVerdict.NoChange, Decide(offense: 0.2, defense: null).Verdict);
        Assert.Equal(ItemVerdictDecider.ReasonOnlyDps, Decide(offense: 2, defense: null).ReasonKey);
    }

    // ---- 从对比结果装配输入（null 的真正来源） ----

    [Fact]
    public void DpsUnavailable_时伤害那一维传_null_而不是_0()
    {
        var result = PobCompareResult.Ok(
            new PobStats(0, 1000, 500),      // 引擎算不出伤害（TotalDPS = 0，回退链也拿不到）
            new PobStats(0, 1100, 500),
            unmappedAffixes: 0);

        Assert.True(result.DpsUnavailable);

        var input = ItemVerdictInput.From(result);

        Assert.Null(input.DpsPercent);
        Assert.Equal(10d, input.EhpPercent);
        Assert.True(input.MetricsPartial);
        Assert.Equal(ItemVerdict.DefenseUpgrade, ItemVerdictDecider.Decide(input).Verdict);
    }

    [Fact]
    public void 能算出伤害时_用对比结果里的百分比()
    {
        var result = PobCompareResult.Ok(
            new PobStats(1000, 1000, 500),
            new PobStats(1100, 900, 500),
            unmappedAffixes: 0);

        var input = ItemVerdictInput.From(result);

        Assert.Equal(10d, input.DpsPercent);
        Assert.Equal(-10d, input.EhpPercent);
        Assert.False(input.MetricsPartial);
        Assert.Equal(ItemVerdict.Tradeoff, ItemVerdictDecider.Decide(input).Verdict);
    }

    [Fact]
    public void 装配输入时会带上抗性守门_与硬门槛()
    {
        var current = new PobStats(1000, 1000, 500)
        {
            FireResist = 80,
            FireResistOver = 5,
        };
        var candidate = new PobStats(1100, 1100, 500)
        {
            FireResist = 76,
            FireResistOver = 1,
        };

        var input = ItemVerdictInput.From(
            PobCompareResult.Ok(current, candidate, unmappedAffixes: 0),
            hardGateFailed: true);

        Assert.True(input.ResistanceDeficitWorsened);
        Assert.True(input.HardGateFailed);
        // 硬门槛优先级更高，所以结论是「有得有失（硬门槛）」
        Assert.Equal(ItemVerdictDecider.ReasonHardGate, ItemVerdictDecider.Decide(input).ReasonKey);
    }

    [Fact]
    public void DpsUnavailable_的输入里_抗性守门仍然生效()
    {
        var current = new PobStats(0, 1000, 500)
        {
            ColdResist = 78,
            ColdResistOver = 3,
        };
        var candidate = new PobStats(0, 1200, 500)
        {
            ColdResist = 70,
            ColdResistOver = 0,
        };

        var input = ItemVerdictInput.From(PobCompareResult.Ok(current, candidate, unmappedAffixes: 0));

        Assert.True(input.MetricsPartial);
        Assert.True(input.ResistanceDeficitWorsened);
        Assert.Equal(ItemVerdictDecider.ReasonResCap, ItemVerdictDecider.Decide(input).ReasonKey);
    }
}
