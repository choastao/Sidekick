namespace Sidekick.Modules.BuildTarget.Services;

using Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 对「从 BD 导入」的模板做语义迁移。
///
/// 背景（用户实测的问题）：早期版本导入 BD 时，把 BD 里那件装备的数值直接写成了该部位的
/// **硬性门槛**（Required=true）。于是「硬性门槛」实际上变成了「你必须穿得和那个 BD 一样」：
/// 用户把自己正穿着的头盔标成当前装备后再去对比，照样被判「不建议」——
/// 因为那件头盔没有 BD 头盔上的能量护盾 / 混沌抗，被算成「硬性门槛未达标」。
///
/// 现在导入的门槛一律是参考值（Required=false），硬性标准只留给用户自己在界面上勾。
/// 老模板靠这个方法做一次性迁移（用 <see cref="BuildTargetTemplate.ImportedTargetsOptional"/> 去重）。
/// </summary>
public static class TargetNormalizer
{
    /// <summary>
    /// 把导入型模板的部位门槛统一降级为参考值。返回 true 表示真的改动了（需要落盘）。
    /// 未导入过的模板（用户自己从零建的硬性门槛）原样保留，绝不擅自改。
    /// </summary>
    public static bool NormalizeImported(BuildTargetTemplate template)
    {
        if (template.ImportedTargetsOptional)
        {
            return false;
        }

        var wasImported = template.EquippedStats.Count > 0
                          || template.EquippedSource.Values.Contains(BaselineSources.Build)
                          || !string.IsNullOrWhiteSpace(template.ImportedFrom);

        template.ImportedTargetsOptional = true;

        if (!wasImported)
        {
            return false;
        }

        foreach (var targets in template.Slots.Values)
        {
            foreach (var target in targets)
            {
                target.Required = false;
            }
        }

        return true;
    }
}
