using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 内置词缀权重表（affix-weights.json）的加载、缓存与建池。
///
/// 只回答一件事：给定「标签集 + 前/后缀 + 物品等级」，这个物品能出哪些词缀。
/// 概率计算交给 <see cref="ExpectedCostCalculator"/>，这里不掺任何推算。
///
/// 数据来自 PoB-PoE2 的重组器实验 + 交易站统计（社区逆向估算），官方不公布成功率，
/// 所以调用方（界面）必须把这份数据的性质讲清楚。
/// </summary>
public class AffixWeightService
{
    private const string FileName = "affix-weights.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ILogger<AffixWeightService> logger;
    private readonly object gate = new();

    private AffixWeightSnapshot? snapshot;

    public AffixWeightService(ILogger<AffixWeightService> logger)
    {
        this.logger = logger;
    }

    /// <summary>随包发布的位置。</summary>
    public static string PackagedPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>用户自己更新权重数据的位置（%AppData%\sidekick\affix-weights.json）。</summary>
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
    public AffixWeightSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot ??= Load();
            }
        }
    }

    public AffixWeightFile? Data => Snapshot.Data;

    public bool HasData => Snapshot.HasData;

    public string? LoadError => Snapshot.LoadError;

    public IReadOnlyList<string> SearchedPaths => Snapshot.SearchedPaths;

    /// <summary>数据来源说明（界面照实展示）。</summary>
    public string Source => Data?.Source ?? "";

    /// <summary>生成方备注（界面照实展示）。</summary>
    public string Note => Data?.Note ?? "";

    /// <summary>词缀总条数。</summary>
    public int ModCount => Data?.Mods.Count ?? 0;

    /// <summary>
    /// 建池：标签集内能出、侧别一致、ilvl 门槛达标（level &lt;= 物品等级）的全部词缀。
    ///
    /// 权重只有 0/1，所以「池」就是条目集合，概率是条数比。
    /// </summary>
    public IReadOnlyList<ModWeight> GetPool(IReadOnlyCollection<string>? tags, AffixSide side, int itemLevel)
    {
        if (Data is null || side == AffixSide.Unknown || tags is null || tags.Count == 0 || itemLevel <= 0)
        {
            return [];
        }

        var pool = new List<ModWeight>();

        foreach (var mod in Data.Mods)
        {
            if (mod.Side != side || mod.Level > itemLevel)
            {
                continue;
            }

            if (mod.SpawnsOn(tags))
            {
                pool.Add(mod);
            }
        }

        return pool;
    }

    private AffixWeightSnapshot Load()
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
                var data = JsonSerializer.Deserialize<AffixWeightFile>(json, JsonOptions);
                if (data is null)
                {
                    logger.LogError("[BuildTarget] Affix weight file is empty: {Path}", path);
                    return new AffixWeightSnapshot
                    {
                        LoadError = "affix-weights.json is empty",
                        SearchedPaths = searched,
                    };
                }

                logger.LogInformation(
                    "[BuildTarget] Affix weights loaded from {Path} ({Count} mods)",
                    path,
                    data.Mods.Count);

                return new AffixWeightSnapshot
                {
                    Data = data,
                    LoadedFrom = path,
                    SearchedPaths = searched,
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BuildTarget] Failed to read affix weights from {Path}", path);
                return new AffixWeightSnapshot
                {
                    LoadError = ex.Message,
                    SearchedPaths = searched,
                };
            }
        }

        logger.LogWarning("[BuildTarget] No affix weight file found. Looked in: {Paths}", string.Join(" | ", searched));
        return new AffixWeightSnapshot
        {
            SearchedPaths = searched,
        };
    }
}
