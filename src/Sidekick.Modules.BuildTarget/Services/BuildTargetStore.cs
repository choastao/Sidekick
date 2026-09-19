using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 目标 BD 模板的持久化（JSON 文件，独立于 Sidekick 的设置库，便于直接复制给朋友）。
/// </summary>
public class BuildTargetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<BuildTargetStore> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    private BuildTargetFile file = new();

    public BuildTargetStore(ILogger<BuildTargetStore> logger)
    {
        this.logger = logger;
        Load();
    }

    /// <summary>模板或当前选中模板发生变化时触发。</summary>
    public event Action? OnChanged;

    public string FilePath => SidekickPaths.GetDataFilePath("build-targets.json");

    public IReadOnlyList<BuildTargetTemplate> Templates => file.Templates;

    public BuildTargetTemplate? Active =>
        file.ActiveId == null ? file.Templates.FirstOrDefault() : file.Templates.FirstOrDefault(x => x.Id == file.ActiveId) ?? file.Templates.FirstOrDefault();

    public BuildTargetTemplate Create(string name)
    {
        var template = new BuildTargetTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新模板" : name.Trim(),
        };

        file.Templates.Add(template);
        file.ActiveId = template.Id;
        Persist();
        return template;
    }

    public BuildTargetTemplate Duplicate(BuildTargetTemplate source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var copy = JsonSerializer.Deserialize<BuildTargetTemplate>(json, JsonOptions) ?? new BuildTargetTemplate();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = source.Name + " 副本";
        copy.UpdatedAt = DateTimeOffset.Now;

        file.Templates.Add(copy);
        Persist();
        return copy;
    }

    public void Delete(string id)
    {
        file.Templates.RemoveAll(x => x.Id == id);
        if (file.ActiveId == id)
        {
            file.ActiveId = file.Templates.FirstOrDefault()?.Id;
        }

        Persist();
    }

    public void SetActive(string id)
    {
        file.ActiveId = id;
        Persist();
    }

    public void Save(BuildTargetTemplate template)
    {
        template.UpdatedAt = DateTimeOffset.Now;
        if (!file.Templates.Any(x => x.Id == template.Id))
        {
            file.Templates.Add(template);
        }

        Persist();
    }

    /// <summary>把一件装备的原始文本记为某部位的当前装备快照。</summary>
    public void CaptureEquipped(BuildTargetTemplate template, string slotKey, string itemText, Item? parsed)
    {
        BaselineSources.MarkManual(template, slotKey, itemText, parsed);
        Save(template);
    }

    /// <summary>
    /// 只在面板上改了值、还没点保存时的落盘。
    /// <paramref name="notify"/> 为 false 时不触发 OnChanged —— 加载期的迁移落盘用它，
    /// 免得以后有人在构造函数里订阅了 OnChanged 而被意外回调。
    /// </summary>
    public void Persist(bool notify = true)
    {
        lock (this)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BuildTarget] Failed to save templates to {Path}", FilePath);
            }
        }

        if (notify)
        {
            OnChanged?.Invoke();
        }
    }

    /// <summary>
    /// JSON 读坏了就先把原文件改名备份再重置 —— 直接 `file = new BuildTargetFile()` 会让
    /// 用户的整个模板库静默消失（CC 审计指出）。备份之后至少还能人工抢救。
    /// </summary>
    private void BackupCorruptFile()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                System.IO.File.Move(FilePath, FilePath + ".bad", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BuildTarget] Failed to back up the corrupt file {Path}", FilePath);
        }
    }

    /// <summary>
    /// 迁移要回写文件，但「一机两号」时另一个实例可能同时在写同一个文件。
    /// 只有磁盘内容还是我们刚读到的这一份时才允许回写；否则放弃落盘
    /// （内存里的迁移照旧生效，下次真正保存时再落）。
    /// </summary>
    private bool FileStillMatches(string jsonWeRead)
    {
        try
        {
            return System.IO.File.Exists(FilePath) && System.IO.File.ReadAllText(FilePath) == jsonWeRead;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BuildTarget] Failed to verify {Path} before the migration write", FilePath);
            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath))
            {
                file = new BuildTargetFile();
                return;
            }

            var json = System.IO.File.ReadAllText(FilePath);
            file = JsonSerializer.Deserialize<BuildTargetFile>(json, JsonOptions) ?? new BuildTargetFile();
            // 一次性迁移：把导入型模板的部位门槛从「硬性」降级为「参考值」。
            // 不迁移的话，老模板会继续把「BD 那件装备的数值」当硬性标准，
            // 于是任何属性不同的装备（包括用户自己正穿着的那件）都被判「不建议」。
            var migratedCount = 0;
            foreach (var template in file.Templates)
            {
                if (TargetNormalizer.NormalizeImported(template))
                {
                    migratedCount++;
                }
            }

            if (migratedCount > 0 && FileStillMatches(json))
            {
                Persist(notify: false);
            }

            logger.LogInformation("[BuildTarget] Loaded {Count} template(s) from {Path} (migrated {Migrated})", file.Templates.Count, FilePath, migratedCount);
        }
        catch (Exception ex)
        {
            BackupCorruptFile();
            logger.LogError(ex, "[BuildTarget] Failed to load templates from {Path}", FilePath);
            file = new BuildTargetFile();
        }
    }
}
