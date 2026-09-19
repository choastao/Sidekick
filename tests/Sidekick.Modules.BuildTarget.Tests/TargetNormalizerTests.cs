using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 老模板迁移：早期版本把「从 BD 导入的门槛」当硬性要求，
/// 于是任何装备属性不同的部位都被判「不建议」。现在导入的门槛是参考值，
/// 老模板要在加载时一次性降级。
/// </summary>
public class TargetNormalizerTests
{
    [Fact]
    public void Imported_targets_are_downgraded_to_reference_values_once()
    {
        var template = new BuildTargetTemplate
        {
            EquippedStats = { [SlotKeys.Helmet] = new Dictionary<string, double> { ["最大能量护盾"] = 58 } },
            EquippedSource = { [SlotKeys.Helmet] = BaselineSources.Build },
            Slots =
            {
                [SlotKeys.Helmet] =
                [
                    new ModTarget { Label = "最大能量护盾", MinValue = 58, Required = true },
                    new ModTarget { Label = "混沌抗性", MinValue = 51, Required = true },
                ],
            },
        };

        Assert.True(TargetNormalizer.NormalizeImported(template));

        Assert.All(template.Slots[SlotKeys.Helmet], x => Assert.False(x.Required));
        Assert.True(template.ImportedTargetsOptional);

        // 幂等：第二次不再改动、也不重复落盘
        Assert.False(TargetNormalizer.NormalizeImported(template));
        Assert.All(template.Slots[SlotKeys.Helmet], x => Assert.False(x.Required));
    }

    [Fact]
    public void A_template_the_user_built_by_hand_keeps_its_required_targets()
    {
        // 没有任何导入痕迹（没抽过数值、没来源标记、没有 importedFrom）→ 是用户手建的，不许动
        var template = new BuildTargetTemplate
        {
            Slots =
            {
                [SlotKeys.Helmet] = [new ModTarget { Label = "火焰抗性", MinValue = 75, Required = true }],
            },
        };

        Assert.False(TargetNormalizer.NormalizeImported(template));
        Assert.True(template.Slots[SlotKeys.Helmet][0].Required);
    }
}
