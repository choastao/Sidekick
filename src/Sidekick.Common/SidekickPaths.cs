namespace Sidekick.Common;

public static class SidekickPaths
{
    public static string GetDataFilePath(string path = "")
    {
        // 多开支持：SIDEKICK_DATA_DIR 可把整套数据（配置/缓存/日志）指向别的目录，
        // 让第二个窗口有自己独立的设置与模板，互不覆盖。
        var overrideFolder = Environment.GetEnvironmentVariable("SIDEKICK_DATA_DIR");
        var sidekickFolder = !string.IsNullOrWhiteSpace(overrideFolder)
                                 ? overrideFolder
                                 : Path.Combine(
                                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                     "sidekick");

        if (!Directory.Exists(sidekickFolder))
        {
            Directory.CreateDirectory(sidekickFolder);
        }

        return !string.IsNullOrEmpty(path) ? Path.Combine(sidekickFolder, path) : sidekickFolder;
    }

    public static string DatabasePath => GetDataFilePath("sidekick.db");
}
