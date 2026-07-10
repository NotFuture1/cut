namespace Cut.Core;

// ffmpeg.exe, ffprobe.exe and libmpv-2.dll are staged next to cut.exe by CI
// and shipped by the installer. No runtime downloads.
public static class ToolPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string Ffmpeg => Path.Combine(BaseDir, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(BaseDir, "ffprobe.exe");
    public static string LibMpv => Path.Combine(BaseDir, "libmpv-2.dll");

    // returns the name of the first missing tool, or null if all present
    public static string? FirstMissing()
    {
        if (!File.Exists(Ffmpeg)) return "ffmpeg.exe";
        if (!File.Exists(Ffprobe)) return "ffprobe.exe";
        if (!File.Exists(LibMpv)) return "libmpv-2.dll";
        return null;
    }
}
