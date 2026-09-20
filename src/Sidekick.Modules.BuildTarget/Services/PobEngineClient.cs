using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// PoB2 无头引擎的 helper 进程客户端。
///
/// <code>
/// 主程序 ──stdio(JSON Lines)──▶ luajit tao-engine-server.lua ──▶ PoB2 引擎（Lua）
/// </code>
///
/// **为什么必须是独立进程**：引擎是几十 MB 的 Lua 数据 + 一跑就是几百毫秒到几秒的重算，
/// 它崩了/卡了不能带崩悬浮窗（用户看到的是「插件坏了」）。进程死了主程序照常能用，
/// 只是这一块显示「引擎没起来」。
///
/// 请求**串行化**：stdio 是单通道，多线程同时写会把两行交错成一个坏 JSON。
/// </summary>
public sealed class PobEngineClient : IDisposable
{
    /// <summary>引擎目录下约定的文件名（helper 脚本是我们自己的，随包分发）。</summary>
    private const string HelperScriptName = "tao-engine-server.lua";

    private const string LuaJitExecutable = "luajit.exe";

    private readonly ILogger<PobEngineClient> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    private Process? process;
    private int nextId;
    private bool disposed;

    public PobEngineClient(ILogger<PobEngineClient> logger)
    {
        this.logger = logger;
    }

    /// <summary>helper 是否活着。引擎没配好 / 已崩溃都是 false。</summary>
    public bool IsRunning => !disposed && process is { HasExited: false };

    /// <summary>引擎目录（找到才有值）。界面用它说明「引擎装在哪」。</summary>
    public string? EngineDirectory { get; private set; }

    /// <summary>最后一次失败原因（启动失败、超时、崩溃），给界面原样显示。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 找引擎目录。顺序：**程序目录下的 <c>pob2/</c>**（随包分发的形态）→
    /// <c>%APPDATA%\sidekick\pob2\</c>（开发期 / 用户自己放的形态）。
    /// 两处都没有就返回 null，界面按「引擎未安装」说，不要瞎猜路径。
    /// </summary>
    public static string? FindEngineDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "pob2"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sidekick", "pob2"),
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, LuaJitExecutable)) && File.Exists(Path.Combine(dir, HelperScriptName)))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// 引擎的 src 目录（<c>HeadlessWrapper.lua</c> 所在处）——helper 的**工作目录**。
    /// 认两种布局：<c>&lt;dir&gt;/pob/src</c>（README 推荐）与 <c>&lt;dir&gt;/src</c>（有人把 clone 内容摊平放）。
    /// 都找不到返回 null，由调用方报「布局不对」，不要瞎猜一个。
    /// </summary>
    private static string? ResolveWorkingDirectory(string directory)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, "pob", "src"),
                     Path.Combine(directory, "src"),
                 })
        {
            if (File.Exists(Path.Combine(candidate, "HeadlessWrapper.lua")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 确保 helper 在跑。已经活着就直接返回 true；否则启动 + ping 一次确认它真的就绪。
    /// **不抛异常**：失败原因写进 <see cref="LastError"/>，由界面决定怎么显示。
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            LastError = "engine client disposed";
            return false;
        }

        if (IsRunning)
        {
            return true;
        }

        var directory = FindEngineDirectory();
        if (directory == null)
        {
            LastError = "PoB2 engine not found (need pob2/luajit.exe + pob2/tao-engine-server.lua)";
            logger.LogWarning("[BuildTarget] PoB engine directory not found");
            return false;
        }

        EngineDirectory = directory;

        try
        {
            // ⚠ 工作目录必须是引擎的 src 目录：HeadlessWrapper.lua 第一行就
            //   dofile("_SimpleGraphic.def.lua")，是相对路径。设错的话 helper 会立刻退出，
            //   主程序看到的是 "engine closed the connection"（本次实测踩到）。
            var workingDirectory = ResolveWorkingDirectory(directory);
            if (workingDirectory == null)
            {
                LastError = $"engine layout looks wrong: no {Path.Combine("pob", "src")}\\HeadlessWrapper.lua under {directory}";
                logger.LogWarning("[BuildTarget] {Error}", LastError);
                return false;
            }

            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(directory, LuaJitExecutable),
                // 脚本用绝对路径传：它的工作目录不是它自己所在的目录
                Arguments = '"' + Path.Combine(directory, HelperScriptName) + '"',
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process = new Process { StartInfo = info };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => logger.LogWarning("[BuildTarget] PoB engine helper exited (code {Code})", SafeExitCode());

            // stderr 必须有人读：管道缓冲满了会把引擎自己卡死。
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                // 引擎正常启动会往 stderr 刷一堆 "Loading .../missing node ..." 噪音，
                // 那些压到 Debug；其它行（Lua 报错、dll 缺失）一律 Warning ——
                // 「helper 一启动就退出」时这一行往往是唯一的线索。
                if (e.Data.StartsWith("Loading", StringComparison.Ordinal) ||
                    e.Data.StartsWith("missing node", StringComparison.Ordinal) ||
                    e.Data.StartsWith("Processing", StringComparison.Ordinal) ||
                    e.Data.StartsWith("Uniques", StringComparison.Ordinal) ||
                    e.Data.StartsWith("Rares", StringComparison.Ordinal) ||
                    e.Data.StartsWith("Startup", StringComparison.Ordinal) ||
                    e.Data.StartsWith("Unicode", StringComparison.Ordinal))
                {
                    logger.LogDebug("[BuildTarget] pob-engine: {Line}", e.Data);
                }
                else
                {
                    logger.LogWarning("[BuildTarget] pob-engine: {Line}", e.Data);
                }
            };

            logger.LogInformation(
                "[BuildTarget] Starting PoB helper: {File} {Args} (cwd {Cwd})",
                info.FileName,
                info.Arguments,
                info.WorkingDirectory);

            if (!process.Start())
            {
                LastError = "failed to start luajit";
                return false;
            }

            process.BeginErrorReadLine();

            // 引擎加载 Data/ 可能要十几秒（首次尤其慢），所以 ping 给足时间。
            var pong = await SendCoreAsync(PobEngineProtocol.MethodPing, null, TimeSpan.FromSeconds(60), cancellationToken);
            if (pong is not { Ok: true })
            {
                LastError = pong?.Error ?? LastError ?? "engine did not answer ping";
                logger.LogWarning("[BuildTarget] PoB engine ping failed: {Error}", LastError);
                StopProcess();
                return false;
            }

            LastError = null;
            logger.LogInformation("[BuildTarget] PoB engine helper ready at {Directory}", directory);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "[BuildTarget] Failed to start the PoB engine helper");
            StopProcess();
            return false;
        }
    }

    /// <summary>
    /// 发一条请求并等应答。失败/超时/进程死掉都返回 null 并把原因写进 <see cref="LastError"/>。
    /// </summary>
    public async Task<PobEngineResponse?> SendAsync(
        string method,
        Dictionary<string, object?>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            LastError = "engine helper is not running";
            return null;
        }

        return await SendCoreAsync(method, parameters, timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
    }

    private async Task<PobEngineResponse?> SendCoreAsync(
        string method,
        Dictionary<string, object?>? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var target = process;
        if (target == null || target.HasExited)
        {
            LastError = "engine helper is not running";
            return null;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var request = new PobEngineRequest
            {
                Id = ++nextId,
                Method = method,
                Params = parameters?.ToDictionary(
                    x => x.Key,
                    x => JsonSerializer.SerializeToElement(x.Value, PobEngineProtocol.Options)),
            };

            await target.StandardInput.WriteLineAsync(PobEngineProtocol.Encode(request));
            await target.StandardInput.FlushAsync();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            try
            {
                // ⚠ 引擎启动时会往 **stdout** 打一堆日志（"Loading main script..."、"missing node ..."），
                //   所以不能「读一行就当应答」。只认以 { 开头、能解出来、且 id 对得上的那一行，
                //   其余当噪音跳过（有超时兜底，不会死循环）。
                while (true)
                {
                    var line = await target.StandardOutput.ReadLineAsync(linked.Token);
                    if (line == null)
                    {
                        LastError = "engine closed the connection";
                        logger.LogWarning("[BuildTarget] PoB engine closed stdout on {Method}", method);
                        return null;
                    }

                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || !trimmed.StartsWith('{'))
                    {
                        logger.LogDebug("[BuildTarget] pob-engine stdout: {Line}", trimmed);
                        continue;
                    }

                    var response = PobEngineProtocol.Decode(trimmed);
                    if (response == null || response.Id != request.Id)
                    {
                        logger.LogDebug("[BuildTarget] pob-engine skipped non-matching line: {Line}", trimmed);
                        continue;
                    }

                    if (!response.Ok)
                    {
                        LastError = response.Error;
                    }

                    return response;
                }
            }
            catch (OperationCanceledException)
            {
                // 超时基本等于引擎卡死（重算进了死循环之类）：留着一个卡住的进程没有意义。
                LastError = $"engine timed out after {timeout.TotalSeconds:0}s on '{method}'";
                logger.LogWarning("[BuildTarget] PoB engine timeout on {Method}", method);
                StopProcess();
                return null;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "[BuildTarget] PoB engine request '{Method}' failed", method);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private int? SafeExitCode()
    {
        try
        {
            return process?.HasExited == true ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private void StopProcess()
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[BuildTarget] Failed to kill the PoB engine helper");
        }
        finally
        {
            process?.Dispose();
            process = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopProcess();
        gate.Dispose();
    }
}
