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

        // 判据只能看「导入来源 / 来源标记」，绝不能看「有没有数值」：
        // MarkManual（游戏内采集）也会写 EquippedStats，所以一条手建模板只要采过一次基准，
        // 就会被误判成导入模板，用户勾的硬性要求会在下次启动时被静默清零且不可逆
        // （CC 复审发现的阻断项）。
        var wasImported = !string.IsNullOrWhiteSpace(template.ImportedFrom)
                          || template.EquippedSource.Values.Contains(BaselineSources.Build)
                          // EquippedStats 只有导入器与 MarkManual 两个写入方，而 MarkManual 必定写
                          // Manual 标记，所以「有数值、却没有对应标记」只可能是旧版导入的遗留数据。
                          || template.EquippedStats.Keys.Any(k => !template.EquippedSource.ContainsKey(k));

        if (!wasImported)
        {
            // 手建模板不打「已迁移」的戳：将来若要再跑一轮迁移，还能覆盖到它。
            return false;
        }

        template.ImportedTargetsOptional = true;

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
