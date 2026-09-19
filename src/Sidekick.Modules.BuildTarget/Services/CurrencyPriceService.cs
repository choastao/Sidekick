using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 内置通货价格表（currency-prices.json）的加载、缓存与查询。
///
/// 这个价格表只用来做「每个词条操作 1 次」的下界估算，不参与任何成功率推算——
/// 词缀池的抽取概率没有权威数据，工具不猜。
/// </summary>
public class CurrencyPriceService
{
    private const string FileName = "currency-prices.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ILogger<CurrencyPriceService> logger;
    private readonly object gate = new();

    private CurrencyPriceSnapshot? snapshot;

    public CurrencyPriceService(ILogger<CurrencyPriceService> logger)
    {
        this.logger = logger;
    }

    /// <summary>随包发布的位置。</summary>
    public static string PackagedPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>用户自己更新价格数据的位置（%AppData%\sidekick\currency-prices.json）。</summary>
    public static string UserPath
    {
        get
        {
            try
            {
                return SidekickPaths.GetDataFilePath(FileName);
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "sidekick",
                    FileName);
            }
        }
    }

    /// <summary>按顺序要查找的位置。</summary>
    public static IReadOnlyList<string> SearchPaths => [PackagedPath, UserPath];

    /// <summary>加载结果（首次访问后缓存，进程内只读盘一次）。</summary>
    public CurrencyPriceSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot ??= Load();
            }
        }
    }

    public CurrencyPriceFile? Data => Snapshot.Data;

    public bool HasData => Snapshot.HasData;

    public string? LoadError => Snapshot.LoadError;

    public IReadOnlyList<string> SearchedPaths => Snapshot.SearchedPaths;

    /// <summary>1 个主计价单位（divine）= 几个副计价单位（chaos）。</summary>
    public double Rate => Data?.Rate > 0 ? Data.Rate : 0;

    /// <summary>混沌石（chaos）。</summary>
    public CurrencyPriceLine? Chaos => Find(Data?.Secondary ?? "chaos");

    /// <summary>神聖石（divine）。</summary>
    public CurrencyPriceLine? Divine => Find(Data?.Primary ?? "divine");

    /// <summary>1 个混沌石折合几个神聖石。</summary>
    public double ChaosInDivine
    {
        get
        {
            var chaos = Chaos;
            if (chaos?.Primary > 0)
            {
                return chaos.Primary;
            }

            return Rate > 0 ? 1 / Rate : 0;
        }
    }

    /// <summary>1 个神聖石折合几个混沌石。</summary>
    public double DivineInChaos
    {
        get
        {
            var divine = Divine;
            if (divine?.Secondary > 0)
            {
                return divine.Secondary;
            }

            return Rate > 0 ? Rate : 0;
        }
    }

    /// <summary>1 个神聖石折合几个神聖石（数据文件缺失时的兜底）。</summary>
    public double DivineInDivine => Divine?.Primary > 0 ? Divine.Primary : 1;

    /// <summary>价格抓取时间距今是否超过 7 天。</summary>
    public bool IsStale
    {
        get
        {
            var fetchedAt = Data?.FetchedAt;
            return fetchedAt.HasValue && DateTimeOffset.Now - fetchedAt.Value > TimeSpan.FromDays(7);
        }
    }

    /// <summary>价格表里的条目总数（三个类目加起来）。</summary>
    public int TotalLineCount => Data is null ? 0 : Data.Categories.Values.Sum(x => x.Lines.Count);

    /// <summary>按 id 找一条（例如 chaos / divine）。</summary>
    public CurrencyPriceLine? Find(string? id)
    {
        if (Data is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var category in Data.Categories.Values)
        {
            var line = category.Lines.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (line != null)
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>Currency 类目里成交量最高的几条。</summary>
    public IReadOnlyList<CurrencyPriceLine> TopCurrencyByVolume(int count)
    {
        if (Data is null || count <= 0)
        {
            return [];
        }

        if (!Data.Categories.TryGetValue("Currency", out var currency))
        {
            // 类目名对不上时退而求其次：拿条目最多的那个类目
            currency = Data.Categories.Values.OrderByDescending(x => x.Lines.Count).FirstOrDefault();
        }

        if (currency is null)
        {
            return [];
        }

        return [.. currency.Lines.OrderByDescending(x => x.Volume).Take(count)];
    }

    /// <summary>
    /// 在所有类目（通货 / 精髓 / 符文）里按中文名或英文名模糊匹配，按成交量排序。
    /// </summary>
    public IReadOnlyList<CurrencyPriceLine> Search(string? query, int max)
    {
        if (Data is null || max <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var keyword = query.Trim();

        return
        [
            .. Data.Categories.Values
                .SelectMany(x => x.Lines)
                .Where(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || x.NameEn.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Volume)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max),
        ];
    }

    /// <summary>模糊匹配的总条数（界面上说明「只显示了前 N 条」）。</summary>
    public int CountMatches(string? query)
    {
        if (Data is null || string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        var keyword = query.Trim();

        return Data.Categories.Values
            .SelectMany(x => x.Lines)
            .Count(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || x.NameEn.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private CurrencyPriceSnapshot Load()
    {
        var searched = new List<string>();

        foreach (var path in SearchPaths)
        {
            searched.Add(path);

            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                // 第一个存在的就用（文件带了 BOM 也能读）
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<CurrencyPriceFile>(json, JsonOptions);
                if (data is null)
                {
                    logger.LogError("[BuildTarget] Currency price file is empty: {Path}", path);
                    return new CurrencyPriceSnapshot
                    {
                        LoadError = "currency-prices.json is empty",
                        SearchedPaths = searched,
                    };
                }

                foreach (var (category, value) in data.Categories)
                {
                    foreach (var line in value.Lines)
                    {
                        line.Category = category;
                    }
                }

                logger.LogInformation("[BuildTarget] Currency prices loaded from {Path}", path);
                return new CurrencyPriceSnapshot
                {
                    Data = data,
                    LoadedFrom = path,
                    SearchedPaths = searched,
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BuildTarget] Failed to read currency prices from {Path}", path);
                return new CurrencyPriceSnapshot
                {
                    LoadError = ex.Message,
                    SearchedPaths = searched,
                };
            }
        }

        logger.LogWarning("[BuildTarget] No currency price file found. Looked in: {Paths}", string.Join(" | ", searched));
        return new CurrencyPriceSnapshot
        {
            SearchedPaths = searched,
        };
    }
}
