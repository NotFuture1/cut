using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Cut.Core;

// runs the ffmpeg passes for one export. progress comes from -progress pipe:1
// (out_time_us lines on stdout) measured against the total seconds of work.
public sealed class ExportJob
{
    public event Action<double>? Progress; // 0..1
    public event Action<string>? Status;

    public async Task<(bool Ok, string? Error)> RunAsync(
        MediaInfo media, SegmentModel segments, ExportSettings settings, CancellationToken ct)
    {
        var tracks = settings.Tracks.Where(t => t.Include).OrderBy(t => t.RelIndex).ToList();
        var container = settings.ResolveContainer(media);
        var staging = Path.Combine(Paths.StagingDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var outDir = Path.GetDirectoryName(settings.OutputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            var kept = segments.Segments.ToList();
            var keptDur = kept.Sum(s => s.Duration);
            if (keptDur <= 0) return (false, "nothing to export");

            var tmpOut = Path.Combine(staging, "out." + container);

            if (settings.Mode == ExportMode.Lossless)
            {
                // pass 1: extract each kept segment (stream copy), pass 2: join
                var total = keptDur * 2;
                double done = 0;
                var segFiles = new List<string>();
                for (int i = 0; i < kept.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Status?.Invoke($"cutting {i + 1}/{kept.Count}");
                    var segPath = Path.Combine(staging, $"seg{i:00000}.mkv");
                    segFiles.Add(segPath);
                    var baseDone = done;
                    var (code, err) = await RunFfmpegAsync(
                        FfmpegArgs.LosslessSegment(media.FilePath, kept[i], tracks, segPath),
                        ct, sec => Report((baseDone + Math.Min(sec, kept[i].Duration)) / total));
                    if (code != 0) return (false, Tail(err));
                    done += kept[i].Duration;
                }

                Status?.Invoke("joining");
                var listFile = Path.Combine(staging, "list.txt");
                await File.WriteAllLinesAsync(listFile, segFiles.Select(FfmpegArgs.ConcatListLine), ct);
                var (jcode, jerr) = await RunFfmpegAsync(
                    FfmpegArgs.ConcatJoin(listFile, tracks, container, tmpOut),
                    ct, sec => Report((keptDur + Math.Min(sec, keptDur)) / total));
                if (jcode != 0) return (false, Tail(jerr));
            }
            else
            {
                Status?.Invoke("encoding");
                var (code, err) = await RunFfmpegAsync(
                    FfmpegArgs.Precise(media.FilePath, kept, tracks, settings.Crf, container, tmpOut),
                    ct, sec => Report(Math.Min(sec, keptDur) / keptDur));
                if (code != 0) return (false, Tail(err));
            }

            File.Move(tmpOut, settings.OutputPath, overwrite: true);
            Report(1);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled");
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) { Logger.Warn($"staging cleanup: {ex.Message}"); }
        }
    }

    private void Report(double fraction) => Progress?.Invoke(Math.Clamp(fraction, 0, 1));

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 4000 ? s : s[^4000..];
    }

    public static async Task<(int Code, string Stderr)> RunFfmpegAsync(
        List<string> args, CancellationToken ct, Action<double>? onSeconds = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolPaths.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Logger.Info($"ffmpeg {string.Join(' ', args)}");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffmpeg");
        using var reg = ct.Register(() =>
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);

        while (await p.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (onSeconds == null) continue;
            // -progress emits key=value lines; out_time_us is microseconds (may be N/A early)
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                onSeconds(us / 1_000_000.0);
        }

        await p.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) Logger.Error($"ffmpeg exit {p.ExitCode}: {Tail(stderr)}");
        return (p.ExitCode, stderr);
    }
}
