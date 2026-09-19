namespace Sidekick.Common.Platform.Interprocess;

public class InterprocessService : IInterprocessService
{
#if DEBUG
    private const string APPLICATION_PROCESS_GUID = "a849007e-44a1-4eac-8cf4-721f3e34c1e8";
#else
    private const string APPLICATION_PROCESS_GUID = "93c46709-7db2-4334-8aa3-28d473e66041";
#endif

    private Mutex? mutex;
    private bool isMainInstance;

    public bool IsAlreadyRunning()
    {
        // 多开支持：SIDEKICK_INSTANCE_ID 给每个窗口一个独立的实例标识，
        // 两个窗口就能同时启动（不设则保持原单实例行为）。
        // SIDEKICK_MULTI_INSTANCE=1 则完全跳过单实例检查。
        var multiInstance = Environment.GetEnvironmentVariable("SIDEKICK_MULTI_INSTANCE");
        if (!string.IsNullOrWhiteSpace(multiInstance) && multiInstance != "0")
        {
            return false;
        }

        var instanceId = Environment.GetEnvironmentVariable("SIDEKICK_INSTANCE_ID");
        var mutexName = string.IsNullOrWhiteSpace(instanceId)
                            ? APPLICATION_PROCESS_GUID
                            : $"{APPLICATION_PROCESS_GUID}-{instanceId}";

        mutex ??= new Mutex(true, mutexName, out isMainInstance);
        return !isMainInstance;
    }

}
