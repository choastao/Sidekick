namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「引擎里已经载入过哪份基准 BD」的缓存。
///
/// ⚠ 缓存键必须是 **(模板 id, 引擎代际号, 评估场景)** 这个三元组，不能只认模板 id。
/// 引擎是**有状态**的（`load_build` 把 BD 载进 Lua 里），进程一旦被杀（超时 / 崩溃 / 重启），
/// 里面那份 BD 就没了；此时只认模板 id 的缓存会命中 → 跳过 `load_build` → `equip` 打到
/// Lua 侧的 `no build loaded` → **此后每一次试穿都失败且无法自愈**（审计 audit-report-c1.md L1）。
///
/// ⚠ 场景（<c>BUILD</c> / <c>MAP</c> / <c>BOSS</c>）也必须进键：切换场景后 Lua 侧那份 BD 的
/// 配置已经被改成另一个场景了，只认模板 id + 代际号的缓存会命中 → 跳过 `load_build` →
/// **拿旧场景的基线去对新场景的候选做差**（面板上的差值全是错的，而且看不出来）。
/// 抽成一个小类是为了让这条判据能被单测直接钉住。
/// </summary>
public sealed class BaselineCache
{
    private string? templateId;
    private int generation = -1;
    private string? context;
    private PobStats? stats;

    /// <summary>缓存命中且仍然有效：同一个模板 **且** 引擎还是同一个进程代次 **且** 同一个评估场景。</summary>
    public bool IsValid(string? templateId, int engineGeneration, string? context) =>
        stats != null
        && this.templateId == templateId
        && generation == engineGeneration
        && string.Equals(this.context, context, StringComparison.OrdinalIgnoreCase);

    /// <summary>缓存里的基准数值（无效时为 null）。</summary>
    public PobStats? Stats => stats;

    public void Store(string? templateId, int engineGeneration, string? context, PobStats? stats)
    {
        this.templateId = templateId;
        generation = engineGeneration;
        this.context = context;
        this.stats = stats;
    }

    public void Invalidate()
    {
        templateId = null;
        generation = -1;
        context = null;
        stats = null;
    }
}
