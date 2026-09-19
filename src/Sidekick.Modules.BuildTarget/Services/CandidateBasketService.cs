using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 备选篮：把「正在挑、还没买」的几件装备放一起算总账（抗性 / 属性够不够）。
///
/// 单例、内存态，同时落盘到 <c>%AppData%\sidekick\candidate-basket.json</c>
/// （多开时跟随 SIDEKICK_DATA_DIR，和设置、目标 BD 模板一样各窗口一份）。
///
/// 和「目标 BD」完全独立：这里只读自己篮子里的数据，不碰模板，也不改任何现有逻辑。
/// </summary>
public class CandidateBasketService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<CandidateBasketService> logger;
    private readonly string? filePathOverride;

    /// <summary>落盘用的锁：快捷键在后台线程加、面板在 UI 线程勾选，别同时写同一个文件。</summary>
    private readonly object fileLock = new();

    private CandidateBasketFile file = new();

    public CandidateBasketService(ILogger<CandidateBasketService> logger) : this(logger, null)
    {
    }

    /// <param name="filePath">测试用：把落盘位置换到临时文件，避免污染真实的备选篮。</param>
    protected CandidateBasketService(ILogger<CandidateBasketService> logger, string? filePath)
    {
        this.logger = logger;
        filePathOverride = filePath;
        Load();
    }

    /// <summary>篮子内容或抗性上限变化时触发。</summary>
    /// <summary>
    /// 列表内容变化时触发。
    ///
    /// ⚠ 契约：本事件**在 fileLock 内**触发（调用方的外层锁还没退，Persist 的内层锁退了而已）。
    /// 当前两个订阅方都是 `_ = InvokeAsync(...)` 即发即忘，不会死锁；
    /// **但如果将来有人写阻塞型订阅方，会和写侧交叉死锁** —— 订阅方必须保持非阻塞。
    /// 这条契约是给备选篮加写侧锁时引入的（复审 2026-09-19 指出）。
    /// </summary>
    public event Action? OnChanged;

    public string FilePath => filePathOverride ?? SidekickPaths.GetDataFilePath("candidate-basket.json");

    /// <summary>
    /// 备选列表的快照。
    ///
    /// 必须返回副本：入篮走快捷键（后台线程），面板在 UI 线程枚举，
    /// 两者同时发生时直接返回 live 列表会抛 "Collection was modified"。
    /// 异常被上层 catch 掉，表现是「这次没写盘 / 组件树渲染炸了」，偶发、难查。
    /// 审计（2026-09-19）发现。
    /// </summary>
    public IReadOnlyList<CandidateBasketItem> Items
    {
        get
        {
            lock (fileLock)
            {
                return [.. file.Items];
            }
        }
    }

    /// <summary>抗性上限，默认 75。改它只影响「✅ 达标 / ⚠️ 差 N」的判定，不改装备数据。</summary>
    public double ResistanceCap
    {
        get => file.ResistanceCap;
        set
        {
            // 写侧必须和读侧（Items 快照）用同一把锁：
            // 只给读侧加锁挡不住「读快照的同时列表被改」，那正是 Collection was modified 的来源。
            // 复审（2026-09-19）指出 Items 快照是「一侧锁」。锁可重入，Persist 内的 lock 不会自锁。
            lock (fileLock)
            {
                file.ResistanceCap = value;
                Persist();
            }
        }
    }

    /// <summary>
    /// 把一件装备加进篮子。同一件（物品文本 hash 相同）重复加入按「覆盖」处理：
    /// 数据按这次解析的结果刷新，位置和勾选状态保留 —— 用户可能特意把某件取消勾选在看别的组合。
    /// </summary>
    public CandidateBasketItem Add(Item item, string slotKey)
    {
        lock (fileLock)
        {
            var id = ComputeId(item.Text.Text);
            var contribution = CandidateBasketCalculator.Contribute(item);
            var name = DisplayName(item);
            var baseType = item.Type ?? item.Definition?.Name ?? "";

            var existing = file.Items.FirstOrDefault(x => x.Id == id);
            if (existing != null)
            {
                existing.Name = name;
                existing.BaseType = baseType;
                existing.SlotKey = slotKey;
                existing.AddedAt = DateTimeOffset.Now;
                existing.Contribution = contribution;
                Persist();
                return existing;
            }

            var entry = new CandidateBasketItem
            {
                Id = id,
                Name = name,
                BaseType = baseType,
                SlotKey = slotKey,
                AddedAt = DateTimeOffset.Now,
                Enabled = true,
                Contribution = contribution,
            };

            file.Items.Add(entry);
            Persist();
            return entry;
        }
    }

    public bool Remove(string id)
    {
        lock (fileLock)
        {
            var removed = file.Items.RemoveAll(x => x.Id == id) > 0;
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (fileLock)
        {
            if (file.Items.Count == 0)
            {
                return;
            }

            file.Items.Clear();
            Persist();
        }
    }

    /// <summary>勾选 / 取消勾选一件备选（合计按勾选的算）。</summary>
    public bool Toggle(string id)
    {
        lock (fileLock)
        {
            var item = file.Items.FirstOrDefault(x => x.Id == id);
            if (item == null)
            {
                return false;
            }

            item.Enabled = !item.Enabled;
            Persist();
            return item.Enabled;
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (fileLock)
        {
            var item = file.Items.FirstOrDefault(x => x.Id == id);
            if (item == null || item.Enabled == enabled)
            {
                return;
            }

            item.Enabled = enabled;
            Persist();
        }
    }

    public CandidateBasketItem? Find(string id)
    {
        lock (fileLock)
        {
            return file.Items.FirstOrDefault(x => x.Id == id);
        }
    }

    /// <summary>物品文本的 hash：同一件装备（甚至从不同地方复制的同一件）算一个 id。</summary>
    public static string ComputeId(string itemText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(itemText ?? ""));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string DisplayName(Item item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name) && !string.Equals(item.Name, item.Type, StringComparison.Ordinal))
        {
            return item.Name!;
        }

        return item.Type ?? item.Name ?? item.Definition?.Name ?? "";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                file = new CandidateBasketFile();
                return;
            }

            var json = File.ReadAllText(FilePath);
            file = JsonSerializer.Deserialize<CandidateBasketFile>(json, JsonOptions) ?? new CandidateBasketFile();
            logger.LogInformation("[Basket] Loaded {Count} candidate(s) from {Path}", file.Items.Count, FilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Basket] Failed to load candidates from {Path}", FilePath);

            // 先留档再重建。不留档的话，下一次任何变更都会把坏文件静默覆写成空篮子，
            // 用户手工改坏或断电写半截的内容就永久没了。审计（2026-09-19）发现。
            TryBackupBrokenFile();
            file = new CandidateBasketFile();
        }
    }

    /// <summary>解析失败时把坏文件改名留档（.bad）。失败也不抛，重建流程照走。</summary>
    private void TryBackupBrokenFile()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var backup = FilePath + ".bad";
            File.Copy(FilePath, backup, overwrite: true);
            logger.LogWarning("[Basket] 原文件解析失败，已留档到 {Backup}", backup);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Basket] 坏文件留档失败，继续重建");
        }
    }

    private void Persist()
    {
        try
        {
            lock (fileLock)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Basket] Failed to save candidates to {Path}", FilePath);
        }

        OnChanged?.Invoke();
    }
}
