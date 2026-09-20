using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidekick.Common;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 功能开关的持久化。刻意独立成一个小 JSON：开关文件读坏时不该连用户的模板库一起丢
/// （与 <see cref="BuildTargetStore"/> 同一个理由，也照它的写法：坏文件先改名备份再重置）。
/// </summary>
public class BuildTargetOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<BuildTargetOptionsStore> logger;
    private readonly string? pathOverride;
    private readonly object fileLock = new();

    private BuildTargetOptions options = new();

    public BuildTargetOptionsStore(ILogger<BuildTargetOptionsStore> logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// 测试用：把开关文件指到指定路径。**别让单测去读写用户真实的 %APPDATA%\sidekick** ——
    /// 那会在开发机上改掉真人的开关（本轮加「关开关要停引擎」的测试时就撞上这个需求）。
    /// </summary>
    internal BuildTargetOptionsStore(ILogger<BuildTargetOptionsStore> logger, string? pathOverride)
    {
        this.logger = logger;
        this.pathOverride = pathOverride;
        Load();
    }

    /// <summary>
    /// 开关变化时触发。⚠ 订阅方必须**立即返回**（只做标记 + 排队 StateHasChanged）：
    /// 事件是同步触发的，阻塞型订阅方会拖住改开关那次 UI 操作。
    /// </summary>
    public event Action? OnChanged;

    public string FilePath => pathOverride ?? SidekickPaths.GetDataFilePath("buildtarget-options.json");

    public bool CraftCost
    {
        get
        {
            lock (fileLock)
            {
                return options.CraftCost;
            }
        }
    }

    public bool PobEngine
    {
        get
        {
            lock (fileLock)
            {
                return options.PobEngine;
            }
        }
    }

    public void SetCraftCost(bool value) => Set(x => x.CraftCost = value);

    public void SetPobEngine(bool value) => Set(x => x.PobEngine = value);

    private void Set(Action<BuildTargetOptions> change)
    {
        lock (fileLock)
        {
            change(options);
            Persist();
        }

        // 事件在锁外触发：订阅方是 Blazor 组件，会同步跑一轮评估；
        // 持锁触发等于给将来埋一个「后台线程持锁等 UI、UI 线程等锁」的交叉死锁
        // （本项目在备选篮上踩过一次，见 skill 里那条「整改带出的新问题」）。
        OnChanged?.Invoke();
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(options, JsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] Failed to save options to {Path}", FilePath);
        }
    }

    /// <summary>读坏就改名备份再重置 —— 别让用户的开关文件静默变成"全关"而没留证据。</summary>
    private void BackupCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Move(FilePath, FilePath + ".bad", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BuildTarget] Failed to back up the corrupt file {Path}", FilePath);
        }
    }

    private void Load()
    {
        lock (fileLock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    options = new BuildTargetOptions();
                    logger.LogInformation("[BuildTarget] Options: no file yet, all switches default to off");
                    return;
                }

                var json = File.ReadAllText(FilePath);
                options = JsonSerializer.Deserialize<BuildTargetOptions>(json, JsonOptions) ?? new BuildTargetOptions();
                logger.LogInformation("[BuildTarget] Options loaded from {Path}: craftCost={CraftCost} pobEngine={PobEngine}", FilePath, options.CraftCost, options.PobEngine);
            }
            catch (Exception ex)
            {
                BackupCorruptFile();
                logger.LogError(ex, "[BuildTarget] Failed to load options from {Path}", FilePath);
                options = new BuildTargetOptions();
            }
        }
    }
}
