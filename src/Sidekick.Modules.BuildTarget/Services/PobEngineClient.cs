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
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(directory, LuaJitExecutable),
                Arguments = '"' + HelperScriptName + '"',
                WorkingDirectory = directory,          // 引擎按相对路径找 Data/，工作目录必须是它
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
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    logger.LogDebug("[BuildTarget] pob-engine: {Line}", e.Data);
                }
            };

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

            string? line;
            try
            {
                line = await target.StandardOutput.ReadLineAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                // 超时基本等于引擎卡死（重算进了死循环之类）：留着一个卡住的进程没有意义。
                LastError = $"engine timed out after {timeout.TotalSeconds:0}s on '{method}'";
                logger.LogWarning("[BuildTarget] PoB engine timeout on {Method}", method);
                StopProcess();
                return null;
            }

            var response = PobEngineProtocol.Decode(line);
            if (response == null)
            {
                LastError = line == null
                    ? "engine closed the connection"
                    : "engine wrote a non-protocol line";
                logger.LogWarning("[BuildTarget] PoB engine sent an unparseable line on {Method}", method);
                return null;
            }

            if (response.Id != request.Id)
            {
                // 串行化前提下不该发生；真发生了说明协议错位，宁可报错也别把别人的数字当自己的。
                LastError = $"engine response id mismatch ({response.Id} != {request.Id})";
                logger.LogWarning("[BuildTarget] PoB engine response id mismatch");
                return null;
            }

            if (!response.Ok)
            {
                LastError = response.Error;
            }

            return response;
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
