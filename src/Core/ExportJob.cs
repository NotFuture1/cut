using System.Diagnostics;
using System.Globalization;

namespace Cut.Core;

public sealed record ExportProgress(double Fraction, double Speed, TimeSpan? Eta);

// runs the ffmpeg passes for one export. progress comes from -progress pipe:1
// (out_time_us lines on stdout) measured against the total seconds of work.
public sealed class ExportJob
{
    public event Action<ExportProgress>? Progress;
    public event Action<string>? Status;

    public ExportPlan? LastPlan { get; private set; }

    public async Task<(bool Ok, string? Error)> RunAsync(
        MediaInfo media, ProjectState state, ExportOptions options, CancellationToken ct)
    {
        var staging = Path.Combine(Paths.StagingDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var outDir = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            if (state.KeptDuration <= 0) return (false, "nothing to export");

            var tmpOut = Path.Combine(staging, "out." + options.ContainerExt);
            var (ok, err) = await RunPlanAsync(media, state, options, staging, tmpOut, ct);

            // hardware paths can fail on odd drivers or files; one retry on the cpu
            if (!ok && err != "cancelled" && options.Mode == ExportMode.Exact
                && (Encoders.IsHardware(options.Encoder) || options.HwDecode))
            {
                Logger.Warn($"export failed with {options.Encoder}/hwdec, retrying on cpu");
                Status?.Invoke("retrying on cpu");
                (ok, err) = await RunPlanAsync(media, state, options.With(VideoEncoder.X264, false), staging, tmpOut, ct);
            }
            if (!ok) return (false, err);

            File.Move(tmpOut, options.OutputPath, overwrite: true);
            Progress?.Invoke(new ExportProgress(1, 0, TimeSpan.Zero));
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

    private async Task<(bool, string?)> RunPlanAsync(MediaInfo media, ProjectState state, ExportOptions options,
        string staging, string tmpOut, CancellationToken ct)
    {
        try
        {
            if (options.Mode == ExportMode.Fast && options.Start == null)
            {
                Status?.Invoke("measuring cuts");
                await MeasureFastAsync(media, state, options, ct);
            }
            var plan = ExportPlan.Build(media, state, options, staging, tmpOut);
            LastPlan = plan;
            if (plan.ListFile != null)
                await File.WriteAllLinesAsync(plan.ListFile, plan.ListLines, ct);
            foreach (var (file, lines) in plan.ExtraLists)
                await File.WriteAllLinesAsync(file, lines, ct);

            var total = plan.Runs.Sum(r => Math.Max(r.Seconds, 0.001));
            double done = 0;
            var clock = Stopwatch.StartNew();
            foreach (var run in plan.Runs)
            {
                ct.ThrowIfCancellationRequested();
                Status?.Invoke(run.Status);
                var baseDone = done;
                var (code, err) = await RunFfmpegAsync(run.Args, ct, sec =>
                {
                    var workDone = baseDone + Math.Min(sec, run.Seconds);
                    var frac = Math.Clamp(workDone / total, 0, 1);
                    var elapsed = clock.Elapsed.TotalSeconds;
                    var speed = elapsed > 0.3 ? workDone / elapsed : 0;
                    TimeSpan? eta = speed > 0 ? TimeSpan.FromSeconds((total - workDone) / speed) : null;
                    Progress?.Invoke(new ExportProgress(frac, speed, eta));
                });
                if (code != 0) return (false, Tail(err));
                done += run.Seconds;
            }
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled");
        }
    }

    // stream copy cut points come from ffmpeg itself; measure them all up front, in parallel
    public static async Task MeasureFastAsync(MediaInfo media, ProjectState state, ExportOptions o, CancellationToken ct)
    {
        var keys = o.Keys ?? new Keyframes(media);
        var starts = state.Segments.Select(s => s.Start).Distinct().ToList();
        var ends = state.Segments.Select(s => s.End).Distinct().ToList();
        var startTasks = starts.ToDictionary(t => t, t => keys.StartAsync(t, ct));
        var endTasks = ends.ToDictionary(t => t, t => keys.EndDtsAsync(t, ct));
        await Task.WhenAll(startTasks.Values.Cast<Task>().Concat(endTasks.Values));
        var sp = startTasks.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
        var ed = endTasks.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
        o.Start = t => sp.TryGetValue(t, out var v) ? v : new StartPoint(t, t);
        o.EndDts = t => ed.TryGetValue(t, out var v) ? v : null;
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 4000 ? s : s[^4000..];
    }

    public static async Task<(int Code, string Stderr)> RunFfmpegAsync(
        List<string> args, CancellationToken ct, Action<double>? onSeconds = null, bool log = true)
    {
        var psi = Proc.Info(ToolPaths.Ffmpeg, args);
        if (log) Logger.Info($"ffmpeg {string.Join(' ', args)}");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffmpeg");
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { /* not allowed, fine */ }
        using var reg = ct.Register(() => Proc.Kill(p));

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
        if (p.ExitCode != 0 && log) Logger.Error($"ffmpeg exit {p.ExitCode}: {Tail(stderr)}");
        return (p.ExitCode, stderr);
    }
}
