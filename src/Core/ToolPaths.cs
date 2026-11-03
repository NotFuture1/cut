namespace Cut.Core;

// ffmpeg.exe, ffprobe.exe (+ their dlls) and libmpv-2.dll are staged next to cut.exe by CI
// and shipped by the installer. No runtime downloads. Off windows (dev tests) the tools
// come from PATH.
public static class ToolPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string Ffmpeg => Find("ffmpeg");
    public static string Ffprobe => Find("ffprobe");
    public static string LibMpv => Path.Combine(BaseDir, "libmpv-2.dll");

    private static string Find(string name)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(BaseDir, name + ".exe");
        var local = Path.Combine(BaseDir, name);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Append("/opt/homebrew/bin"))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return name;
    }

    // returns the name of the first missing tool, or null if all present
    public static string? FirstMissing()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!File.Exists(Ffmpeg)) return "ffmpeg.exe";
        if (!File.Exists(Ffprobe)) return "ffprobe.exe";
        if (!File.Exists(LibMpv)) return "libmpv-2.dll";
        return null;
    }
}
