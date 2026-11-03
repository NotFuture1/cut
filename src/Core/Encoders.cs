using System.Globalization;

namespace Cut.Core;

public enum VideoEncoder { X264, Nvenc, Qsv, Amf }

// picks the fastest h.264 encoder this machine can actually run. detection does a real
// half-second encode with the exact flags exports use, so a driver that can't take
// those flags is never chosen. runs once in the background at startup.
public static class Encoders
{
    private static Task<VideoEncoder>? _detect;

    public static Task<VideoEncoder> Best => _detect ??= Task.Run(DetectAsync);

    public static void StartDetection() => _ = Best;

    public static string Label(VideoEncoder e) => e switch
    {
        VideoEncoder.Nvenc => "nvidia nvenc",
        VideoEncoder.Qsv => "intel quick sync",
        VideoEncoder.Amf => "amd amf",
        _ => "cpu (x264)",
    };

    public static bool IsHardware(VideoEncoder e) => e != VideoEncoder.X264;

    public static string PixFmt(VideoEncoder e) => e == VideoEncoder.Qsv ? "nv12" : "yuv420p";

    private static string B(long bps) => bps.ToString(CultureInfo.InvariantCulture);

    // video codec args for a target average bitrate. maxrate/bufsize give the encoder room
    // for action scenes without letting the file overshoot the size estimate much.
    public static List<string> Args(VideoEncoder e, long bitrate, double fps)
    {
        var gop = Math.Clamp((int)Math.Round((fps > 0 ? fps : 30) * 2), 2, 600).ToString(CultureInfo.InvariantCulture);
        var max = B((long)(bitrate * 1.5));
        var buf = B(bitrate * 2);
        var args = e switch
        {
            VideoEncoder.Nvenc => new List<string>
            {
                "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr",
                "-b:v", B(bitrate), "-maxrate", max, "-bufsize", buf, "-spatial-aq", "1",
                "-profile:v", "high",
            },
            VideoEncoder.Qsv => new List<string>
            {
                "-c:v", "h264_qsv", "-preset", "medium",
                "-b:v", B(bitrate), "-maxrate", max, "-bufsize", buf, "-profile:v", "high",
            },
            VideoEncoder.Amf => new List<string>
            {
                "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak",
                "-b:v", B(bitrate), "-maxrate", max, "-bufsize", buf, "-profile:v", "high",
            },
            // slower preset only when bits are scarce: that's where it visibly pays off
            _ => new List<string>
            {
                "-c:v", "libx264", "-preset", bitrate < 2_500_000 ? "medium" : "veryfast",
                "-b:v", B(bitrate), "-maxrate", max, "-bufsize", buf, "-profile:v", "high",
            },
        };
        args.AddRange(new[] { "-g", gop, "-pix_fmt", PixFmt(e) });
        return args;
    }

    private static string CacheFile => Path.Combine(Paths.AppDataDir, "encoder.txt");

    // the answer only changes with the gpu driver or ffmpeg: re-check weekly or when ffmpeg changes
    private static string Stamp()
    {
        try { return File.GetLastWriteTimeUtc(ToolPaths.Ffmpeg).Ticks.ToString(CultureInfo.InvariantCulture); }
        catch { return "?"; }
    }

    private static async Task<VideoEncoder> DetectAsync()
    {
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CUT_HW_DETECT") != "1")
            return VideoEncoder.X264;
        try
        {
            if (File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < TimeSpan.FromDays(7))
            {
                var parts = File.ReadAllText(CacheFile).Split('|');
                if (parts.Length == 2 && parts[0] == Stamp() && Enum.TryParse<VideoEncoder>(parts[1], out var cached))
                {
                    Logger.Info($"video encoder (cached): {cached}");
                    return cached;
                }
            }
        }
        catch { /* re-detect */ }
        var found = await ProbeAllAsync();
        try { Paths.EnsureBaseDirs(); File.WriteAllText(CacheFile, $"{Stamp()}|{found}"); } catch { /* fine */ }
        return found;
    }

    // a failed hardware export forgets the cached choice so the next launch probes again
    public static void Forget()
    {
        try { File.Delete(CacheFile); } catch { /* fine */ }
    }

    private static async Task<VideoEncoder> ProbeAllAsync()
    {
        await Task.Delay(1500); // let startup and the first file open go first

        var candidates = new[] { VideoEncoder.Nvenc, VideoEncoder.Qsv, VideoEncoder.Amf };
        var tasks = candidates.Select(async e =>
        {
            var args = new List<string>
            {
                "-hide_banner", "-v", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=0.5",
            };
            args.AddRange(Args(e, 2_000_000, 30));
            args.AddRange(new[] { "-f", "null", "-" });
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var (code, err) = await ExportJob.RunFfmpegAsync(args, cts.Token, log: false);
                if (code != 0) Logger.Info($"encoder {e} unavailable: {err.Trim().Split('\n').LastOrDefault()}");
                return code == 0;
            }
            catch (Exception ex)
            {
                Logger.Info($"encoder {e} probe failed: {ex.Message}");
                return false;
            }
        }).ToArray();

        var ok = await Task.WhenAll(tasks);
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!ok[i]) continue;
            Logger.Info($"video encoder: {candidates[i]}");
            return candidates[i];
        }
        Logger.Info("video encoder: x264");
        return VideoEncoder.X264;
    }
}
