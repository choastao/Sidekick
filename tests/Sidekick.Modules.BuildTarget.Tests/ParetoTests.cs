using Sidekick.Modules.BuildTarget.Models;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 候选相对当前装备的帕累托判定（<see cref="Pareto.Compare"/>）的守门员。
///
/// 重点是**容差边界**（±0.5%）与**顺序**：支配要先判 —— 在 (dps=+0.5, ehp=-0.5) 这种
/// 两边的条件都成立的位置上，先判支配才与「两维都不差、至少一维更好」的定义一致。
/// </summary>
public class ParetoTests
{
    [Theory]
    [InlineData(2d, 0.5)]
    [InlineData(0.5, 2d)]
    [InlineData(5d, 5d)]
    [InlineData(0d, 1d)]
    // 边界：ehp 正好 -0.5% 仍算「不差」，dps 正好 +0.5% 算「更好」→ 支配
    [InlineData(0.5, -0.5)]
    [InlineData(-0.5, 0.5)]
    public void 两维都不差且至少一维更好_支配(double dps, double ehp)
    {
        Assert.Equal(ParetoStatus.Dominates, Pareto.Compare(dps, ehp));
    }

    [Theory]
    [InlineData(-2d, -0.5)]
    [InlineData(-0.5, -2d)]
    [InlineData(-5d, -5d)]
    [InlineData(-6d, 0d)]
    public void 两维都不优且至少一维更差_被支配(double dps, double ehp)
    {
        Assert.Equal(ParetoStatus.Dominated, Pareto.Compare(dps, ehp));
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0.4, -0.4)]
    [InlineData(-0.49, 0.49)]
    public void 两维都在容差内_一样(double dps, double ehp)
    {
        Assert.Equal(ParetoStatus.Equal, Pareto.Compare(dps, ehp));
    }

    [Theory]
    [InlineData(2d, -2d)]
    [InlineData(-2d, 2d)]
    [InlineData(0.6, -0.6)]
    public void 一维更好一维更差_有得有失(double dps, double ehp)
    {
        Assert.Equal(ParetoStatus.Tradeoff, Pareto.Compare(dps, ehp));
    }

    [Fact]
    public void 某一维算不出来_无从判断支配_归_Unknown()
    {
        // null = 引擎算不出那个数 → **Unknown**（「判不了」），
        // 不能归 Tradeoff —— 那会显示成「一维更好、一维更差」，是把缺数说成了结论。
        Assert.Equal(ParetoStatus.Unknown, Pareto.Compare(null, 10));
        Assert.Equal(ParetoStatus.Unknown, Pareto.Compare(10, null));
        Assert.Equal(ParetoStatus.Unknown, Pareto.Compare(-10, null));
        Assert.Equal(ParetoStatus.Unknown, Pareto.Compare(null, null));
    }

    [Fact]
    public void 容差是_0_5_不是_1()
    {
        Assert.Equal(0.5, Pareto.TolerancePercent);

        // 0.6% 已经超出容差 → 在另一维下降时判「有得有失」，不再被吞进「一样」
        Assert.Equal(ParetoStatus.Tradeoff, Pareto.Compare(0.6, -0.6));

        // 0.5% 正好在容差的边沿 → 算「不差 / 更好」
        Assert.Equal(ParetoStatus.Dominates, Pareto.Compare(0.5, 0));
    }
}
