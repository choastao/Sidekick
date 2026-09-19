using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 内置词缀类型表（affix-types.json）的加载、缓存与查询。
///
/// 只回答一件事：这条词缀是**前缀**还是**后缀**。物品解析结果里没有这个信息
/// （PoE 的物品文本不写前后缀），而它直接决定配哪张预兆（左旋 / 右旋），
/// 所以宁可从游戏数据导出的事实表里查，也不按关键词猜。
/// 查不到就是「未知」，界面显示「（类型未知）」并且不给预兆建议。
/// </summary>
public class AffixTypeService
{
    private const string FileName = "affix-types.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 去掉数值与标点。词缀库里的文本长这样：「增加#%冰冷傷害」——数字卡在词组中间，
    /// 直接做子串包含匹配不上「增加冰冷傷害」，所以两边都归一化后再比。
    /// </summary>
    private static readonly Regex Noise = new(@"[\s\d#%+\-–—.,:;，。、：；]", RegexOptions.Compiled);

    /// <summary>关键词表只建一次：原始词 + 归一化词 + 对应预设。</summary>
    private static readonly List<(string Keyword, string Normalized, StatPresets.Preset Preset)> KeywordIndex =
    [
        .. StatPresets.All
            .SelectMany(preset => preset.Keywords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(keyword => (Keyword: keyword, Normalized: Normalize(keyword), Preset: preset))),
    ];

    private readonly ILogger<AffixTypeService> logger;
    private readonly object gate = new();

    private AffixTypeSnapshot? snapshot;

    public AffixTypeService(ILogger<AffixTypeService> logger)
    {
        this.logger = logger;
    }

    /// <summary>随包发布的位置。</summary>
    public static string PackagedPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>用户自己更新词缀数据的位置（%AppData%\sidekick\affix-types.json）。</summary>
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
    public AffixTypeSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot ??= Load();
            }
        }
    }

    public AffixTypeFile? Data => Snapshot.Data;

    public bool HasData => Snapshot.HasData;

    public string? LoadError => Snapshot.LoadError;

    public IReadOnlyList<string> SearchedPaths => Snapshot.SearchedPaths;

    /// <summary>按预设 Key 取（Key 与 StatPresets 一致）。</summary>
    public AffixTypePreset? Find(string? key)
    {
        if (Data is null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Data.Presets.TryGetValue(key, out var preset) ? preset : null;
    }

    /// <summary>
    /// 按「显示名 + 匹配关键词」找词缀类型。找不到返回 null，调用方按未知处理。
    ///
    /// 模板里的词缀目标有两种来源：从预设加的（Label 就是预设中文名），和从词缀库搜索
    /// 加的（Label 是游戏里的整行文本）。两条路都试：先精确比预设名，再用关键词最长匹配。
    /// </summary>
    public AffixTypePreset? Resolve(IEnumerable<string>? keywords, string? label)
    {
        if (Data is null)
        {
            return null;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(label))
        {
            candidates.Add(label.Trim());
        }

        if (keywords != null)
        {
            candidates.AddRange(keywords.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // 1. 预设中文名（或数据里的英文关键词）和候选文本完全相等
        foreach (var candidate in candidates)
        {
            var hit = Data.Presets.Values.FirstOrDefault(x => string.Equals(x.Label, candidate, StringComparison.OrdinalIgnoreCase))
                      ?? Data.Presets.Values.FirstOrDefault(x => string.Equals(x.EnglishKeyword, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                return hit;
            }
        }

        // 2. 走 StatPresets 的关键词表（中/繁/英三语都在里面），取命中的最长关键词，
        //    避免「護甲」把「增加護甲」也一起吃掉这类模糊冲突。
        //    整行文本（词缀库里加的目标）先把数值和标点去掉再比。
        StatPresets.Preset? bestPreset = null;
        var bestLength = 0;
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate);

            foreach (var entry in KeywordIndex)
            {
                if (entry.Keyword.Length <= bestLength)
                {
                    continue;
                }

                if (candidate.Contains(entry.Keyword, StringComparison.OrdinalIgnoreCase)
                    || (normalized.Length > 0 && normalized.Contains(entry.Normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    bestPreset = entry.Preset;
                    bestLength = entry.Keyword.Length;
                }
            }
        }

        if (bestPreset != null)
        {
            return Find(bestPreset.Key);
        }

        // 3. 退一步：数据里的英文关键词出现在候选文本里
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate);
            var hit = Data.Presets.Values
                          .Where(x => !string.IsNullOrWhiteSpace(x.EnglishKeyword)
                                      && (candidate.Contains(x.EnglishKeyword, StringComparison.OrdinalIgnoreCase)
                                          || normalized.Contains(Normalize(x.EnglishKeyword), StringComparison.OrdinalIgnoreCase)))
                          .OrderByDescending(x => x.EnglishKeyword.Length)
                          .FirstOrDefault();
            if (hit != null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string Normalize(string? text) => string.IsNullOrWhiteSpace(text)
                                                        ? ""
                                                        : Noise.Replace(text, "");

    private AffixTypeSnapshot Load()
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
                var data = JsonSerializer.Deserialize<AffixTypeFile>(json, JsonOptions);
                if (data is null)
                {
                    logger.LogError("[BuildTarget] Affix type file is empty: {Path}", path);
                    return new AffixTypeSnapshot
                    {
                        LoadError = "affix-types.json is empty",
                        SearchedPaths = searched,
                    };
                }

                logger.LogInformation(
                    "[BuildTarget] Affix types loaded from {Path} ({Count} presets)",
                    path,
                    data.Presets.Count);

                return new AffixTypeSnapshot
                {
                    Data = data,
                    LoadedFrom = path,
                    SearchedPaths = searched,
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BuildTarget] Failed to read affix types from {Path}", path);
                return new AffixTypeSnapshot
                {
                    LoadError = ex.Message,
                    SearchedPaths = searched,
                };
            }
        }

        logger.LogWarning("[BuildTarget] No affix type file found. Looked in: {Paths}", string.Join(" | ", searched));
        return new AffixTypeSnapshot
        {
            SearchedPaths = searched,
        };
    }
}
