namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 评估场景：引擎载入 BD 时**在内存里临时覆盖**的敌人设定（见 <c>Engine/tao-engine-server.lua</c>）。
///
/// 三个标签就是协议里的字面量，Lua 侧按同一个字面量分派 —— 两边**不许各写一套拼写**，
/// 所以这里集中定义，界面下拉的取值也用它。
///
/// ⚠ 覆盖只影响**本次载入**的内存态配置，绝不写回用户的 BD 源码；切场景 = 下次载入生效
///   （BD 是载入那一刻算的），且缓存键里带着它（见 <see cref="BaselineCache"/>）。
/// </summary>
public static class PobContexts
{
    /// <summary>按 BD 自己的配置算（默认值，行为与加场景之前完全一致）。</summary>
    public const string Build = "BUILD";

    /// <summary>刷图：敌人 82 级、非 Boss。</summary>
    public const string Map = "MAP";

    /// <summary>打王：敌人 84 级 Boss。</summary>
    public const string Boss = "BOSS";

    /// <summary>
    /// 只认 <c>BUILD</c> / <c>MAP</c> / <c>BOSS</c>（大小写与首尾空白容错）；
    /// 别的值（含 null / 空串 / 用户手改坏的 JSON）**一律当 BUILD** ——
    /// 宁可按 BD 原样算，也不要拿一个我们自己都不认识的标签去让引擎改配置。
    /// </summary>
    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        Map => Map,
        Boss => Boss,
        _ => Build,
    };

    /// <summary>场景的界面文案键（<c>Context_*</c>）。</summary>
    public static string ResourceKey(string? value) => Normalize(value) switch
    {
        Map => "Context_Map",
        Boss => "Context_Boss",
        _ => "Context_Build",
    };
}
