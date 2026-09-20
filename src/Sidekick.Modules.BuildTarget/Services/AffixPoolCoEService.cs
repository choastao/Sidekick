using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// Craft of Exile 的 PoE2 做装模型数据（affix-pool-coe.json）的加载、缓存与建池。
///
/// 与 <see cref="AffixWeightService"/> 的分工：
///   · 这个服务算概率：给定「底材 id + 前/后缀 + 物品等级」，返回带**真实权重**的档位条目；
///   · affix-weights.json 那份（权重只有 0/1）继续给词缀浏览 / 搜索用，两条路互不干扰。
///
/// 数据来源：Craft of Exile（craftofexile.com）的 PoE2 数据，官方不公布成功率，
/// 所以调用方（界面）必须把这份数据的性质讲清楚。
/// </summary>
public class AffixPoolCoEService
{
    private const string FileName = "affix-pool-coe.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ILogger<AffixPoolCoEService> logger;
    private readonly object gate = new();

    private AffixPoolCoeSnapshot? snapshot;

    public AffixPoolCoEService(ILogger<AffixPoolCoEService> logger)
    {
        this.logger = logger;
    }

    /// <summary>随包发布的位置。</summary>
    public static string PackagedPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>用户自己更新做装数据的位置（%AppData%\sidekick\affix-pool-coe.json）。</summary>
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
    public AffixPoolCoeSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot ??= Load();
            }
        }
    }

    public AffixPoolCoeFile? Data => Snapshot.Data;

    public bool HasData => Snapshot.HasData;

    public string? LoadError => Snapshot.LoadError;

    public IReadOnlyList<string> SearchedPaths => Snapshot.SearchedPaths;

    /// <summary>数据来源说明（界面照实展示）。</summary>
    public string Source => Data?.Source ?? "";

    /// <summary>生成方的模型说明。</summary>
    public string Model => Data?.Model ?? "";

    /// <summary>词缀总条数。</summary>
    public int ModCount => Data?.Mods.Count ?? 0;

    /// <summary>底材总个数。</summary>
    public int BaseCount => Data?.Bases.Count ?? 0;

    /// <summary>
    /// 建池：某个底材上、侧别一致、ilvl 达标（ilvl &lt;= 物品等级）且权重 &gt; 0 的全部档位条目。
    ///
    /// 一个词缀族的每个档位是一条独立条目（它们各自带权重，加权求和就是这么来的）；
    /// 底材 id 判不出来（null / 空 / 数据里没有）时返回空池，调用方按「池不可用」处理。
    /// </summary>
    public IReadOnlyList<AffixPoolEntry> GetPool(string? baseId, AffixSide side, int itemLevel)
    {
        if (Data is null
            || string.IsNullOrWhiteSpace(baseId)
            || side == AffixSide.Unknown
            || itemLevel <= 0)
        {
            return [];
        }

        var pool = new List<AffixPoolEntry>();

        foreach (var mod in Data.Mods)
        {
            if (mod.Side != side || !mod.Bases.TryGetValue(baseId, out var tiers) || tiers is null)
            {
                continue;
            }

            foreach (var tier in tiers)
            {
                if (tier.Ilvl > itemLevel || tier.W <= 0)
                {
                    continue;
                }

                pool.Add(new AffixPoolEntry
                {
                    Family = mod.Family,
                    Text = mod.Text,
                    Side = side,
                    ItemLevel = tier.Ilvl,
                    Min = tier.Min,
                    Max = tier.Max,
                    Weight = tier.W,
                });
            }
        }

        return pool;
    }

    private AffixPoolCoeSnapshot Load()
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
                var data = JsonSerializer.Deserialize<AffixPoolCoeFile>(json, JsonOptions);
                if (data is null)
                {
                    logger.LogError("[BuildTarget] Affix pool data file is empty: {Path}", path);
                    return new AffixPoolCoeSnapshot
                    {
                        LoadError = "affix-pool-coe.json is empty",
                        SearchedPaths = searched,
                    };
                }

                logger.LogInformation(
                    "[BuildTarget] Affix pool data loaded from {Path} ({Bases} bases, {Count} mods)",
                    path,
                    data.Bases.Count,
                    data.Mods.Count);

                return new AffixPoolCoeSnapshot
                {
                    Data = data,
                    LoadedFrom = path,
                    SearchedPaths = searched,
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BuildTarget] Failed to read affix pool data from {Path}", path);
                return new AffixPoolCoeSnapshot
                {
                    LoadError = ex.Message,
                    SearchedPaths = searched,
                };
            }
        }

        logger.LogWarning("[BuildTarget] No affix pool data file found. Looked in: {Paths}", string.Join(" | ", searched));
        return new AffixPoolCoeSnapshot
        {
            SearchedPaths = searched,
        };
    }
}

/// <summary>
/// 做装数据的加载结果：要么拿到数据，要么带着「为什么没有」的原因，绝不静默失败。
/// </summary>
public class AffixPoolCoeSnapshot
{
    public AffixPoolCoeFile? Data { get; init; }

    public string? LoadedFrom { get; init; }

    public string? LoadError { get; init; }

    public IReadOnlyList<string> SearchedPaths { get; init; } = [];

    public bool HasData => Data != null;
}
