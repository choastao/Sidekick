using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 「这件装备该不该换」的判定语义。这一组测试是从一个真实误判来的：
///
/// 用户把自己正穿着的头盔标成当前装备后再去对比，界面判「不建议」——
/// 原因有两层：① 从 BD 导入的门槛被当成了硬性要求；② 「这件装备没有该条属性」
/// 被当成「不达标」。结果「硬性门槛」等价于「你必须穿得和那个 BD 一模一样」。
///
/// 现在的语义：硬性门槛只可能是用户自己勾的；「没这条属性」= 不涉及；
/// 主轴是相对当前装备的增减。
/// </summary>
public class VerdictSemanticsTests
{
    private static readonly TestLocalizer Zh = new();

    private static ModCheck Check(
        string label,
        double min,
        double? newValue,
        double? current = null,
        bool required = false) => new()
    {
        Label = label,
        MinValue = min,
        Required = required,
        New = newValue,
        Current = current,
    };

    [Fact]
    public void An_item_without_any_target_stat_is_not_a_failure()
    {
        // 用户实例：护甲 / 闪避底子的头盔，完全没有 BD 那件头盔的能量护盾与混沌抗。
        var evaluation = new SlotEvaluation
        {
            HasEquipped = true,
            Checks =
            [
                Check("最大能量护盾", 58, newValue: null),
                Check("增加能量护盾", 81, newValue: null),
                Check("混沌抗性", 51, newValue: null),
            ],
        };

        var (verdict, headline) = VerdictDecider.Decide(evaluation, Zh);

        // 关键：不能判「不建议」——这件装备只是不涉及这几条，不是不合格。
        Assert.NotEqual(Verdict.Bad, verdict);
        Assert.Equal(Verdict.Warn, verdict);
        Assert.Equal(Zh["Result_None_Provided"].Value, headline);
    }

    [Fact]
    public void A_required_minimum_the_user_set_still_fails()
    {
        // 用户自己勾的硬性要求（例如「这个部位我必须 ≥75 火抗」）没达到 → 该红就红
        var evaluation = new SlotEvaluation
        {
            HasEquipped = true,
            Checks = [Check("火焰抗性", 75, newValue: 30, current: 30, required: true)],
        };

        var (verdict, headline) = VerdictDecider.Decide(evaluation, Zh);

        Assert.Equal(Verdict.Bad, verdict);
        Assert.Contains("火焰抗性", headline, StringComparison.Ordinal);
    }

    [Fact]
    public void The_item_you_are_already_wearing_says_so_instead_of_advising_a_swap()
    {
        // 用户把身上那件复制了一遍：数值两边一样（增减为 0），且已认定是同一件
        var evaluation = new SlotEvaluation
        {
            HasEquipped = true,
            IsCurrentItem = true,
            Checks = [Check("最大生命", 70, newValue: 60, current: 60)],
        };

        var (verdict, headline) = VerdictDecider.Decide(evaluation, Zh);

        Assert.Equal(Verdict.Good, verdict);
        Assert.Equal(Zh["Result_Is_Current"].Value, headline);
    }

    [Fact]
    public void Verdict_follows_the_delta_against_the_current_item()
    {
        // 全部提升 → 建议换上
        var better = new SlotEvaluation
        {
            HasEquipped = true,
            Checks =
            [
                Check("最大生命", 70, newValue: 90, current: 60),
                Check("火焰抗性", 40, newValue: 45, current: 30),
            ],
        };
        Assert.Equal(Verdict.Good, VerdictDecider.Decide(better, Zh).Verdict);

        // 有得有失 → 可留观
        var mixed = new SlotEvaluation
        {
            HasEquipped = true,
            Checks =
            [
                Check("最大生命", 70, newValue: 90, current: 60),
                Check("火焰抗性", 40, newValue: 20, current: 30),
            ],
        };
        Assert.Equal(Verdict.Warn, VerdictDecider.Decide(mixed, Zh).Verdict);

        // 至少一条下降且没有提升 → 更差
        var worse = new SlotEvaluation
        {
            HasEquipped = true,
            Checks = [Check("最大生命", 70, newValue: 40, current: 60)],
        };
        Assert.Equal(Verdict.Bad, VerdictDecider.Decide(worse, Zh).Verdict);

        // 一样 → 差不多
        var even = new SlotEvaluation
        {
            HasEquipped = true,
            Checks = [Check("最大生命", 70, newValue: 60, current: 60)],
        };
        Assert.Equal(Verdict.Warn, VerdictDecider.Decide(even, Zh).Verdict);
    }

    [Fact]
    public void Missing_baseline_is_reported_as_such_and_never_as_worse()
    {
        var evaluation = new SlotEvaluation
        {
            HasEquipped = false,
            Checks = [Check("最大生命", 70, newValue: 90)],
        };

        var (verdict, headline) = VerdictDecider.Decide(evaluation, Zh);

        Assert.Equal(Verdict.Warn, verdict);
        Assert.Equal(Zh["Result_Pass_No_Baseline"].Value, headline);
    }

    [Fact]
    public void New_strings_exist_in_both_languages_with_no_placeholders()
    {
        var english = new TestLocalizer(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var key in new[] { "Result_Is_Current", "Result_None_Provided", "Stat_Not_Provided_Note" })
        {
            Assert.False(Zh[key].ResourceNotFound, $"{key} 缺中文");
            Assert.False(english[key].ResourceNotFound, $"{key} 缺英文");
        }
    }
}
