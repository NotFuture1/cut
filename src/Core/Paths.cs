namespace Cut.Core;

public static class Paths
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cut");

    public static string ConfigFile => Path.Combine(AppDataDir, "config.json");
    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static string StagingDir => Path.Combine(AppDataDir, "staging");
    public static string SelfTestDir => Path.Combine(AppDataDir, "selftest");

    public static void EnsureBaseDirs()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
