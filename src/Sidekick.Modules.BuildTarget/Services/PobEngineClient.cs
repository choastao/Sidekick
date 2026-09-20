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
    private readonly BuildTargetOptionsStore options;
    private readonly SemaphoreSlim gate = new(1, 1);

    private Process? process;
    private int nextId;
    private bool disposed;
    private int generation;

    public PobEngineClient(ILogger<PobEngineClient> logger, BuildTargetOptionsStore options)
    {
        this.logger = logger;
        this.options = options;

        // 开关一关就**真的把 helper 停掉**：否则「关」只是不再调用，进程还常驻吃内存（几百 MB 的
        // PoB 数据在 Lua 里）—— 那是假关。用户问过「开关是否支持热开关」，答案必须是
        // 「是，而且关掉会把引擎卸掉，下次打开重新冷启」。
        // ⚠ 订阅方必须立即返回（见 BuildTargetOptionsStore.OnChanged 的契约）：杀进程丢线程池，
        //   别占着用户拨开关那次 UI 操作。
        options.OnChanged += OnOptionsChanged;
    }

    /// <summary>
    /// 开关拨动后的反应。**打开时什么都不做**（引擎在第一次真要用时才懒启动，见 EnsureStartedAsync）；
    /// 关掉时才停进程。方向被 <c>EngineSwitchTests</c> 钉住 —— 反过来（开着就把引擎杀掉）是最坏的错法。
    /// </summary>
    private void OnOptionsChanged()
    {
        if (!ShouldStopForOptions(options.PobEngine, IsRunning))
        {
            return;
        }

        _ = Task.Run(Stop);
    }

    /// <summary>
    /// 「这次开关变化要不要停引擎」—— 抽成纯函数是为了能单测：真杀进程在单测里没法验，
    /// 但「只有关掉才停、打开绝不停」这个方向必须被钉住。
    /// </summary>
    internal static bool ShouldStopForOptions(bool engineEnabled, bool running) => !engineEnabled && running;

    /// <summary>
    /// 停掉 helper，释放它占的内存。没在跑时是空操作。
    /// 停掉之后**下次要用会重新冷启动**（约 1.5 秒 + 载入 Data 的时间），
    /// 且 <see cref="Generation"/> 会 +1，任何「引擎里载入过哪份 BD」的缓存随之失效（自愈）。
    /// </summary>
    public void Stop()
    {
        if (process == null)
        {
            return;
        }

        logger.LogInformation("[BuildTarget] Stopping PoB engine helper (generation {Generation})", Generation);
        StopProcess();
    }

    /// <summary>helper 是否活着。引擎没配好 / 已崩溃都是 false。</summary>
    public bool IsRunning => !disposed && process is { HasExited: false };

    /// <summary>
    /// 引擎进程的**代际号**：每次启动成功、每次杀进程都 +1。
    ///
    /// 为什么需要它：引擎是**有状态**的（`load_build` 把 BD 载进 Lua 里），而进程一重启，
    /// 引擎里的 BD 就没了。任何「引擎里已经载入过什么」的缓存都必须把它一起当缓存键 ——
    /// 只认模板 id 的缓存会在引擎被杀之后**永久失效**：缓存命中 → 跳过 load_build →
    /// `equip` 打到 Lua 侧 `no build loaded` → 此后每一次试穿都失败，且没有自愈路径
    /// （审计 audit-report-c1.md 的 L1，C2 批量会整批报销）。
    /// </summary>
    public int Generation => Volatile.Read(ref generation);

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
            Interlocked.Increment(ref generation);
            logger.LogInformation(
                "[BuildTarget] PoB engine helper ready at {Directory} (generation {Generation})",
                directory,
                Generation);
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

            // ⚠ 超时 CTS 必须**在写之前**建好（审计 L2）：写也要受超时保护。
            //   触发形状是「子进程活着但不读 stdin」——正是超时要防的卡死形态：
            //   load_build 的 XML 可有几十~几百 KB，远超匿名管道默认缓冲，
            //   原先无 token 的 WriteLineAsync 会**无限等待**，还握着串行闸，连超时兜底都没有。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            try
            {
                await target.StandardInput.WriteLineAsync(PobEngineProtocol.Encode(request).AsMemory(), linked.Token);
                await target.StandardInput.FlushAsync(linked.Token);

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
                // ⚠ 必须区分「超时」与「调用方取消」（审计 L3）：C2 的批量会传 token，
                //   若把用户取消也当成引擎卡死，取消一次批量就会杀掉引擎 —— 并顺着 L1
                //   变成「之后每次试穿都失败」。只有超时才杀进程。
                var timedOut = linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                if (timedOut)
                {
                    // 超时基本等于引擎卡死（重算进了死循环之类）：留着一个卡住的进程没有意义。
                    LastError = $"engine timed out after {timeout.TotalSeconds:0}s on '{method}'";
                    logger.LogWarning("[BuildTarget] PoB engine timeout on {Method}", method);
                    StopProcess();
                }
                else
                {
                    LastError = $"engine request '{method}' cancelled by the caller";
                    logger.LogInformation("[BuildTarget] PoB engine request {Method} cancelled by the caller", method);
                }

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

            // 进程没了 = 引擎里载入过的 BD 也没了，代际号 +1（见 Generation 的说明）
            Interlocked.Increment(ref generation);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        options.OnChanged -= OnOptionsChanged;
        StopProcess();
        gate.Dispose();
    }
}
