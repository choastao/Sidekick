using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 与 PoB2 helper 进程之间的协议：**JSON Lines over stdio**（一行一个 JSON 对象，双向）。
///
/// 为什么不用本地 HTTP：helper 只服务本进程，开端口就得处理端口占用、防火墙、别的程序
/// 乱连；stdio 的生命周期跟着父进程走，父进程一死子进程的管道就断了（不会留孤儿进程）。
///
/// 这一层只负责编解码，不碰引擎细节 —— 引擎调用的具体字段名由 helper 的 Lua 侧决定，
/// 主程序只按方法名取值（见 <see cref="PobEngineResponse.GetDouble"/>）。
/// </summary>
public sealed class PobEngineRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>参数用 JsonElement 承载：不同方法参数形状不同，避免为每个方法写一个 DTO。</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement>? Params { get; set; }
}

/// <summary>helper 的应答。ok=false 时 error 里是它自己给的原因（引擎报错原样带回来）。</summary>
public sealed class PobEngineResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("result")]
    public Dictionary<string, JsonElement>? Result { get; set; }

    /// <summary>取一个字符串结果；缺字段或类型不对都返回 null（不抛，调用方按"没有这个值"处理）。</summary>
    public string? GetString(string key) =>
        Result != null && Result.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>取一个数值结果；JSON null / 缺字段 / 非数值一律返回 null。</summary>
    public double? GetDouble(string key)
    {
        if (Result == null || !Result.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    }

    /// <summary>取一个嵌套对象结果（helper 把数值放在 result.stats 里，不是顶层）。</summary>
    public Dictionary<string, JsonElement>? GetObject(string key)
    {
        if (Result != null && Result.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value.Deserialize<Dictionary<string, JsonElement>>();
        }

        return null;
    }

    public bool? GetBool(string key) =>
        Result != null && Result.TryGetValue(key, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;
}

public static class PobEngineProtocol
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ---- 方法名（Lua 侧必须同名，改这里要同步改 tao-engine-server.lua） ----

    /// <summary>载入基准 BD（params: xml）。</summary>
    public const string MethodLoadBuild = "load_build";

    /// <summary>把一件物品试穿到某个槽位（params: slot, item）。</summary>
    public const string MethodEquip = "equip";

    /// <summary>把某个槽位恢复成基准 BD 里原来的那件（params: slot）。</summary>
    public const string MethodRestoreSlot = "restore_slot";

    /// <summary>读当前算完的数值（DPS / EHP 等）。</summary>
    public const string MethodStats = "stats";

    /// <summary>只回一个 pong，用于探活。</summary>
    public const string MethodPing = "ping";

    /// <summary>让 helper 自己退出。主程序退出前会发一次，避免留孤儿进程。</summary>
    public const string MethodShutdown = "shutdown";

    public static string Encode(PobEngineRequest request) => JsonSerializer.Serialize(request, Options);

    /// <summary>
    /// 解一行应答。**解不出来返回 null 而不是抛**：helper 崩了 / 引擎往 stdout 打了别的东西时，
    /// 调用方需要的是「这一行不是合法应答」这个事实，而不是一个异常。
    /// </summary>
    public static PobEngineResponse? Decode(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PobEngineResponse>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
