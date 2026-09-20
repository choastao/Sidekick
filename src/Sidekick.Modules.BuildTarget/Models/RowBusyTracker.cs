namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 按行记录「正在算」的状态。**判据是每行的代次令牌，不是行 id 本身**。
///
/// 病根：同一行可以在**上一条计算还没回来**时被重新发起（重新展开那一行；或列表被重算 /
/// 换了目标 BD、清空之后又被展开）。此时上一条的收尾若按 id 清标记，就会把**新那一条刚设上的
/// 忙碌标记**一起清掉，界面随即落到「这条算不了」的分支 —— 那是一条不成立的结论（它其实还在算），
/// 要等下一条算完才自愈。所以收尾必须交回发起时拿到的令牌，只在「这一行还是我这一轮」时才清。
///
/// 抽成一个小类是为了让这条判据能被单测直接钉住（组件里的状态没法单独测）。
/// </summary>
public sealed class RowBusyTracker
{
    private readonly Dictionary<string, long> tokens = new();
    private long next;

    /// <summary>这一行当前是否正在计算（界面按它决定显示「正在试穿」还是结果）。</summary>
    public bool IsBusy(string id) => tokens.ContainsKey(id);

    /// <summary>标记该行开始计算，返回本次的令牌（收尾时原样交回 <see cref="End"/>）。</summary>
    public long Begin(string id)
    {
        var token = ++next;
        tokens[id] = token;
        return token;
    }

    /// <summary>
    /// 收尾，只在「这一行的当前令牌还是我这次拿到的那一个」时清。
    /// 已被 <see cref="Clear"/> 清掉、或已被新一次计算接管的行：**什么都不做**
    /// （清了就等于把还在飞的那一轮的忙碌状态抹掉）。
    /// </summary>
    public void End(string id, long token)
    {
        if (tokens.TryGetValue(id, out var current) && current == token)
        {
            tokens.Remove(id);
        }
    }

    /// <summary>整批作废（列表重算 / 换目标 BD）：连同在飞行的那几行一起不再被认。</summary>
    public void Clear() => tokens.Clear();
}
