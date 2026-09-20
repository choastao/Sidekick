using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 把导入结果（<see cref="PobBuildImporter"/> 造出来的那份模板）逐字段搬到真正落盘的模板上。
///
/// ⚠ 这段逻辑原先写在设置页的 <c>ImportBuild</c> 里，靠一行行手抄字段 —— 已经漏过三次
/// （`EquippedSource`、`ImportedTargetsOptional`、`PobXml`），每次的后果都是「界面说假话」。
/// 最近这一次（2026-09-20 第三轮审计的阻断项）漏的是 `PobXml`：
/// 任何经界面导入的模板都没有 BD 源码 → 引擎试穿 / 备选篮排序 / 逐条词缀收益全部走
/// `NoBuildXml`，而提示还叫用户「重新导入一次 BD」，重导也救不回来（死循环）。
///
/// 所以搬到可被测试直接调用的地方，并配一条反射测试
/// （`ImportedTemplateMapperTests.Apply_覆盖模板的全部可写属性`）：
/// **以后给模板加字段，忘了在这里抄一行，测试会红** —— 这是唯一能挡住第四次的办法。
/// </summary>
public static class ImportedTemplateMapper
{
    /// <summary>
    /// 把 <paramref name="source"/>（导入结果）的全部业务字段写到 <paramref name="target"/>（落盘的模板）上。
    ///
    /// 有意**不**搬的两个字段：
    ///   · <c>Id</c> —— 落盘模板有自己的身份（由 <see cref="BuildTargetStore.Create"/> 生成）；
    ///   · <c>UpdatedAt</c> —— 由 <see cref="BuildTargetStore.Save"/> 负责，不是导入内容的一部分。
    /// 这两条与反射测试里的排除清单一一对应，改这里要同时改测试的排除清单。
    /// </summary>
    public static void Apply(BuildTargetTemplate target, BuildTargetTemplate source)
    {
        target.Name = source.Name;
        target.Character = source.Character;
        target.Slots = source.Slots;
        target.Equipped = source.Equipped;
        target.EquippedStats = source.EquippedStats;
        target.EquippedNames = source.EquippedNames;
        // 漏这一行会让导入的基准全部显示成「来源未知」，与「导入 BD 自带基准」的说法矛盾。
        target.EquippedSource = source.EquippedSource;
        target.ImportedFrom = source.ImportedFrom;
        // 漏这面旗子，新模板会被当成「手建模板」，将来再跑迁移就够不着它。
        target.ImportedTargetsOptional = source.ImportedTargetsOptional;
        // 漏这一行 = C1 试穿对比 / C2a 排序 / C2b·C2c 逐条收益对**所有经界面导入的模板**全部不可达。
        target.PobXml = source.PobXml;
    }
}
