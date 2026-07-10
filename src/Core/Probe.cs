using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Cut.Core;

public static class Probe
{
    public static async Task<MediaInfo> RunAsync(string file, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolPaths.Ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", file })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffprobe");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var json = await stdoutTask;
        _ = await stderrTask;

        if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"ffprobe failed (exit {p.ExitCode}) on {file}");

        var raw = JsonSerializer.Deserialize<ProbeResult>(json)
                  ?? throw new InvalidOperationException("ffprobe returned unparseable json");
        return ToMediaInfo(file, raw);
    }

    private static MediaInfo ToMediaInfo(string file, ProbeResult raw)
    {
        var info = new MediaInfo { FilePath = file };

        if (double.TryParse(raw.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
            info.Duration = dur;

        int audioRel = 0;
        foreach (var s in raw.Streams ?? new List<ProbeStream>())
        {
            switch (s.CodecType)
            {
                case "video" when !info.HasVideo:
                    // attached cover art also probes as "video"; a real video stream has a frame rate
                    info.HasVideo = true;
                    info.VideoCodec = s.CodecName ?? "";
                    info.Width = s.Width ?? 0;
                    info.Height = s.Height ?? 0;
                    info.Fps = ParseFps(s.AvgFrameRate);
                    break;
                case "audio":
                    info.AudioTracks.Add(new AudioTrackInfo
                    {
                        RelIndex = audioRel++,
                        Codec = s.CodecName ?? "audio",
                        Channels = s.Channels ?? 0,
                        Title = s.Tags?.Title,
                        Language = s.Tags?.Language,
                    });
                    break;
            }
        }
        return info;
    }

    private static double ParseFps(string? avgFrameRate)
    {
        if (string.IsNullOrEmpty(avgFrameRate)) return 0;
        var parts = avgFrameRate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
            return num / den;
        if (double.TryParse(avgFrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return 0;
    }
}
