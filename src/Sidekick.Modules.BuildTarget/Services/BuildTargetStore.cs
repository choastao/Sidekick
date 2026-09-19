using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
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
    public void CaptureEquipped(BuildTargetTemplate template, string slotKey, string itemText)
    {
        template.Equipped[slotKey] = itemText;
        Save(template);
    }

    /// <summary>只在面板上改了值、还没点保存时的落盘。</summary>
    public void Persist()
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

        OnChanged?.Invoke();
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
            logger.LogInformation("[BuildTarget] Loaded {Count} template(s) from {Path}", file.Templates.Count, FilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] Failed to load templates from {Path}", FilePath);
            file = new BuildTargetFile();
        }
    }
}
