using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Cut.Core;

public static class Probe
{
    private static readonly HashSet<string> ImageCodecs = new()
    {
        "png", "mjpeg", "jpegls", "webp", "bmp", "tiff", "jpeg2000", "qoi", "pam", "ppm", "targa", "avif",
    };

    private static readonly HashSet<string> ImageExts = new()
    {
        "png", "jpg", "jpeg", "webp", "bmp", "tif", "tiff", "jfif", "avif", "qoi", "tga",
    };

    public static async Task<MediaInfo> RunAsync(string file, CancellationToken ct = default)
    {
        var json = await Proc.RunTextAsync(ToolPaths.Ffprobe, new[]
        {
            "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", file,
        }, ct);

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"ffprobe failed on {file}");

        var raw = JsonSerializer.Deserialize<ProbeResult>(json)
                  ?? throw new InvalidOperationException("ffprobe returned unparseable json");
        return ToMediaInfo(file, raw);
    }

    private static double D(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long L(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static MediaInfo ToMediaInfo(string file, ProbeResult raw)
    {
        var info = new MediaInfo
        {
            FilePath = file,
            Duration = D(raw.Format?.Duration),
            StartTime = D(raw.Format?.StartTime),
            FormatBitRate = L(raw.Format?.BitRate),
            FileSize = L(raw.Format?.Size),
        };
        if (info.FileSize <= 0)
        {
            try { info.FileSize = new FileInfo(file).Length; } catch { /* not a local file */ }
        }

        int audioRel = 0;
        foreach (var s in raw.Streams ?? new List<ProbeStream>())
        {
            switch (s.CodecType)
            {
                // attached cover art also probes as "video"; skip it
                case "video" when !info.HasVideo && s.Disposition?.AttachedPic != 1:
                    info.HasVideo = true;
                    info.VideoCodec = s.CodecName ?? "";
                    info.Width = s.Width ?? 0;
                    info.Height = s.Height ?? 0;
                    info.Fps = ParseFps(s.AvgFrameRate);
                    if (info.Fps <= 0 || info.Fps > 1000) info.Fps = ParseFps(s.RFrameRate);
                    info.VideoBitRate = L(s.BitRate);
                    info.IsStillImage = ImageCodecs.Contains(info.VideoCodec)
                        && (L(s.NbFrames) <= 1 || ImageExts.Contains(info.ContainerExt));
                    break;
                case "audio":
                    info.AudioTracks.Add(new AudioTrackInfo
                    {
                        RelIndex = audioRel++,
                        Codec = s.CodecName ?? "audio",
                        Channels = s.Channels ?? 0,
                        SampleRate = (int)L(s.SampleRate),
                        BitRate = L(s.BitRate),
                        Title = s.Tags?.TrackName,
                        Language = s.Tags?.Language,
                    });
                    break;
            }
        }
        if (info.IsStillImage) info.Duration = 0;
        return info;
    }

    private static double ParseFps(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2)
        {
            var num = D(parts[0]);
            var den = D(parts[1]);
            return den > 0 ? num / den : 0;
        }
        return D(rate);
    }
}

// small process helpers shared by probe, keyframes, waveforms, thumbnails
public static class Proc
{
    public static ProcessStartInfo Info(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<string> RunTextAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Process.Start(Info(exe, args)) ?? throw new InvalidOperationException($"failed to start {exe}");
        using var reg = ct.Register(() => Kill(p));
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None);
        var text = await stdout;
        _ = await stderr;
        ct.ThrowIfCancellationRequested();
        return p.ExitCode == 0 ? text : "";
    }

    public static async Task<byte[]> RunBytesAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Process.Start(Info(exe, args)) ?? throw new InvalidOperationException($"failed to start {exe}");
        using var reg = ct.Register(() => Kill(p));
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None);
        _ = await stderr;
        ct.ThrowIfCancellationRequested();
        return p.ExitCode == 0 ? ms.ToArray() : Array.Empty<byte>();
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
