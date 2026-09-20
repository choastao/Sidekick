namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 目标 BD 模块的功能开关（存 <c>%APPDATA%\sidekick\buildtarget-options.json</c>）。
///
/// 目的：不用的部分不要默认吃资源 —— 关着的功能**连数据文件都不会去读**
/// （例如洗练成本要读 0.75 MB 的 <c>affix-pool-coe.json</c>）。
///
/// 两项都**默认关**：想用哪个就到 设置 → 目标BD → 功能开关 打开。
/// 改成默认开时请同时改 <c>BuildTargetOptionsTests.Defaults_are_off</c> ——
/// 那条测试就是用来钉住这个约定的。
/// </summary>
public sealed class BuildTargetOptions
{
    /// <summary>洗练成本 / 成功率面板（需要 affix-pool-coe.json 与 affix-types.json）。</summary>
    public bool CraftCost { get; set; }

    /// <summary>PoB2 无头引擎（试穿对比 DPS / EHP）。尚未实现，开关先占位。</summary>
    public bool PobEngine { get; set; }
}
