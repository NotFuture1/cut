namespace Cut.Core;

public static class Logger
{
    private static readonly object Lock = new();
    private static string _file = "";
    private const long MaxBytes = 5 * 1024 * 1024;

    public static string LogFile => _file;

    public static void Init()
    {
        Paths.EnsureBaseDirs();
        _file = Path.Combine(Paths.LogsDir, "cut.log");
        Info("---- cut starting ----");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        if (string.IsNullOrEmpty(_file)) return;
        lock (Lock)
        {
            try
            {
                var fi = new FileInfo(_file);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(_file, Path.Combine(Paths.LogsDir, "cut.old.log"), overwrite: true);
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch
            {
                // logging must never take the app down
            }
        }
    }
}
