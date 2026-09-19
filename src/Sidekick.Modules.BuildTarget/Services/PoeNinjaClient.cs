using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>从 poe.ninja 角色页链接里解析出的三元组。</summary>
public sealed record PoeNinjaTarget(string League, string Account, string Character);

public enum PoeNinjaError
{
    None,

    /// <summary>index-state 里没有这个 url。</summary>
    LeagueNotFound,

    /// <summary>404：角色不存在，或该账号的资料未公开。</summary>
    CharacterNotFound,

    /// <summary>200 但没有 pathOfBuildingExport。</summary>
    NoExportCode,

    /// <summary>其它网络/状态码问题（含限流 429）。</summary>
    HttpError,
}

public sealed record PoeNinjaResult(string? Code, PoeNinjaError Error, string? Detail);

/// <summary>
/// 把 poe.ninja 的角色页链接换成 PoB 分享码。
///
/// 为什么不去抓角色页 HTML：poe.ninja 是 Astro 站点，页面本身不带导出码，
/// 数据得走它的两个公开 JSON 接口。接口里最容易踩的坑是
/// <c>url</c> 与 <c>snapshotName</c> 不是同一个串（url=forbiddenrites，snapshotName=forbidden-rites）：
/// 链接里出现的是 <c>url</c>，而角色的 overview 参数必须传 <c>snapshotName</c>，传错会 404。
/// 所以这里必须查表映射，不能自己拼。
///
/// 另一个坑是 <c>version</c> 每天轮换，绝不能写死。每次导入都按需重新取，
/// 靠下面的短时缓存避免用户连点导入时反复打接口。
/// </summary>
public sealed class PoeNinjaClient
{
    private const string IndexStateUrl = "https://poe.ninja/poe2/api/data/index-state";

    /// <summary>index-state 一天只变一次，但取一次是一次网络往返；短时缓存足够挡住连点。</summary>
    private static readonly TimeSpan IndexCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>缓存：联赛 url（即链接里的那一段）-&gt; (version, snapshotName)。</summary>
    private static readonly object IndexCacheLock = new();
    private static Dictionary<string, (string Version, string SnapshotName)>? indexCache;
    private static DateTimeOffset indexCacheExpiresAt;

    /// <summary>
    /// 角色页链接：/poe2/builds/{league}/character/{account}/{character}。
    /// scheme / www / query / fragment / 结尾斜杠都可有可无；账号与角色名可能是 URL 编码的，交给调用方解码。
    /// </summary>
    private static readonly Regex CharacterUrlRegex = new(
        @"^(?:https?://)?(?:www\.)?poe\.ninja/poe2/builds/(?<league>[^/?#]+)/character/(?<account>[^/?#]+)/(?<character>[^/?#]+)/?(?:[?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 是不是 poe.ninja 角色页链接；是则解析出三元组。
    /// 不是本工具的输入就返回 false —— 必须让别的解析路径继续工作。
    /// </summary>
    public static bool TryParseCharacterUrl(string input, out PoeNinjaTarget target)
    {
        target = null!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var match = CharacterUrlRegex.Match(input.Trim());
        if (!match.Success)
        {
            return false;
        }

        // 链接里的账号/角色名可能被编码过（曾实测到 %23），解码后再用，否则账号名会原样带 %23 去打接口。
        var league = Uri.UnescapeDataString(match.Groups["league"].Value);
        var account = Uri.UnescapeDataString(match.Groups["account"].Value);
        var character = Uri.UnescapeDataString(match.Groups["character"].Value);

        if (string.IsNullOrWhiteSpace(league) || string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(character))
        {
            return false;
        }

        target = new PoeNinjaTarget(league, account, character);
        return true;
    }

    /// <summary>
    /// 取该角色的 PoB 导出码。
    /// 任何网络/HTTP 异常都不许往外抛，一律转成 <see cref="PoeNinjaResult"/> 里的错误。
    /// </summary>
    public async Task<PoeNinjaResult> ResolveExportCodeAsync(PoeNinjaTarget target, CancellationToken ct = default)
    {
        if (target == null ||
            string.IsNullOrWhiteSpace(target.League) ||
            string.IsNullOrWhiteSpace(target.Account) ||
            string.IsNullOrWhiteSpace(target.Character))
        {
            return new PoeNinjaResult(null, PoeNinjaError.HttpError, "缺少联赛 / 账号 / 角色名");
        }

        try
        {
            var index = await GetIndexAsync(ct);
            if (!index.TryGetValue(target.League, out var league))
            {
                return new PoeNinjaResult(null, PoeNinjaError.LeagueNotFound, target.League);
            }

            // overview 必须是 snapshotName（带连字符），不是链接里的 url。
            var requestUrl =
                $"https://poe.ninja/poe2/api/builds/{Uri.EscapeDataString(league.Version)}/character" +
                $"?account={Uri.EscapeDataString(target.Account)}" +
                $"&name={Uri.EscapeDataString(target.Character)}" +
                $"&overview={Uri.EscapeDataString(league.SnapshotName)}";

            using var response = await HttpClient.GetAsync(requestUrl, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 角色不存在 / 资料未公开都走这里，返回的是 ASP.NET 的 JSON 错误体。
                return new PoeNinjaResult(null, PoeNinjaError.CharacterNotFound, "404");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PoeNinjaResult(null, PoeNinjaError.HttpError, DescribeStatus(response));
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty("pathOfBuildingExport", out var export)
                           ? export.GetString()
                           : null;

            if (string.IsNullOrWhiteSpace(code))
            {
                return new PoeNinjaResult(null, PoeNinjaError.NoExportCode, null);
            }

            return new PoeNinjaResult(code.Trim(), PoeNinjaError.None, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户主动取消：这不属于"网络问题"，按惯例往上抛，交给调用方的取消流程处理。
            throw;
        }
        catch (Exception ex)
        {
            return new PoeNinjaResult(null, PoeNinjaError.HttpError, ex.Message);
        }
    }

    /// <summary>
    /// 取 index-state（url -&gt; version/snapshotName）。缓存命中就不打接口，
    /// 因为导入是用户反复点的操作，而这份数据本身每天只变一次。
    /// </summary>
    private static async Task<Dictionary<string, (string Version, string SnapshotName)>> GetIndexAsync(CancellationToken ct)
    {
        lock (IndexCacheLock)
        {
            if (indexCache != null && DateTimeOffset.UtcNow < indexCacheExpiresAt)
            {
                return indexCache;
            }
        }

        // 故意不把网络请求放进锁里：并发导入时最多多打一次接口，好过让所有人排队等一次超时。
        var json = await HttpClient.GetStringAsync(IndexStateUrl, ct);
        var parsed = ParseIndexState(json);

        lock (IndexCacheLock)
        {
            indexCache = parsed;
            indexCacheExpiresAt = DateTimeOffset.UtcNow + IndexCacheTtl;
            return indexCache;
        }
    }

    /// <summary>
    /// 解析 index-state。只取用得到的三段：url（链接里的联赛段）、version、snapshotName。
    /// 内部可见是为了让测试用真实夹具离线验证映射，不必联网。
    /// </summary>
    internal static Dictionary<string, (string Version, string SnapshotName)> ParseIndexState(string json)
    {
        // 联赛段比对忽略大小写，所以键比较器也这么配。
        var map = new Dictionary<string, (string Version, string SnapshotName)>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("snapshotVersions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var item in versions.EnumerateArray())
        {
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            var version = item.TryGetProperty("version", out var v) ? v.GetString() : null;
            var snapshot = item.TryGetProperty("snapshotName", out var s) ? s.GetString() : null;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(snapshot))
            {
                continue;
            }

            map[url] = (version, snapshot);
        }

        return map;
    }

    /// <summary>把状态码转成人能读的失败原因；限流额外带上 Retry-After。</summary>
    private static string DescribeStatus(HttpResponseMessage response)
    {
        var detail = $"HTTP {(int)response.StatusCode}";
        if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is { } retry)
        {
            detail += retry.Delta is { } delta
                          ? $"，Retry-After {delta.TotalSeconds:0}s"
                          : "，Retry-After 见响应头";
        }

        return detail;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TAO-BuildTarget/1.0");
        return client;
    }
}
