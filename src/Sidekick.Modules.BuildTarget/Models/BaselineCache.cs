namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「引擎里已经载入过哪份基准 BD」的缓存。
///
/// ⚠ 缓存键必须是 **(模板 id, 引擎代际号)** 这个两元组，不能只认模板 id。
/// 引擎是**有状态**的（`load_build` 把 BD 载进 Lua 里），进程一旦被杀（超时 / 崩溃 / 重启），
/// 里面那份 BD 就没了；此时只认模板 id 的缓存会命中 → 跳过 `load_build` → `equip` 打到
/// Lua 侧的 `no build loaded` → **此后每一次试穿都失败且无法自愈**（审计 audit-report-c1.md L1）。
/// 抽成一个小类是为了让这条判据能被单测直接钉住。
/// </summary>
public sealed class BaselineCache
{
    private string? templateId;
    private int generation = -1;
    private PobStats? stats;

    /// <summary>缓存命中且仍然有效：同一个模板 **且** 引擎还是同一个进程代次。</summary>
    public bool IsValid(string? templateId, int engineGeneration) =>
        stats != null && this.templateId == templateId && generation == engineGeneration;

    /// <summary>缓存里的基准数值（无效时为 null）。</summary>
    public PobStats? Stats => stats;

    public void Store(string? templateId, int engineGeneration, PobStats? stats)
    {
        this.templateId = templateId;
        generation = engineGeneration;
        this.stats = stats;
    }

    public void Invalidate()
    {
        templateId = null;
        generation = -1;
        stats = null;
    }
}
