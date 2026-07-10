using System.Diagnostics;

namespace Cut.Core;

// headless smoke test run by CI: cut.exe --selftest
// proves, on real windows: tools are staged, libmpv loads through our P/Invoke
// surface, and both export modes produce correct output. exit 0 = pass.
public static class SelfTest
{
    private static string _log = "";

    public static int Run()
    {
        Paths.EnsureBaseDirs();
        _log = Path.Combine(Paths.LogsDir, "selftest.log");
        File.WriteAllText(_log, "");
        var sw = Stopwatch.StartNew();
        try
        {
            Step1Tools();
            Step2LibMpv();
            var fixture = Step3Fixture();
            Step4ProbeFixture(fixture);
            Step5LosslessExport(fixture);
            Step6PreciseExport(fixture);
            Log($"PASS all steps in {sw.Elapsed.TotalSeconds:0.0}s");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FAIL: {ex}");
            return 1;
        }
    }

    private static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        File.AppendAllText(_log, line + Environment.NewLine);
        Logger.Info($"selftest: {msg}");
    }

    private static void Require(bool cond, string what)
    {
        if (!cond) throw new InvalidOperationException(what);
        Log($"ok: {what}");
    }

    private static void Step1Tools()
    {
        var missing = ToolPaths.FirstMissing();
        Require(missing == null, $"tools present beside exe{(missing != null ? $" (missing {missing})" : "")}");
        foreach (var exe in new[] { ToolPaths.Ffmpeg, ToolPaths.Ffprobe })
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi)!;
            var first = p.StandardOutput.ReadLine();
            p.WaitForExit();
            Require(p.ExitCode == 0, $"{Path.GetFileName(exe)} runs: {first}");
        }
    }

    private static void Step2LibMpv()
    {
        using var mpv = new MpvPlayer(wid: null);
        var version = mpv.GetPropertyString("mpv-version");
        Require(!string.IsNullOrEmpty(version), $"libmpv loads: {version}");
    }

    private static string Step3Fixture()
    {
        Directory.CreateDirectory(Paths.SelfTestDir);
        var fixture = Path.Combine(Paths.SelfTestDir, "test.mkv");
        var (code, err) = ExportJob.RunFfmpegAsync(FfmpegArgs.Fixture(fixture), CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(code == 0 && File.Exists(fixture), $"fixture generated{(code != 0 ? $": {err}" : "")}");
        return fixture;
    }

    private static void Step4ProbeFixture(string fixture)
    {
        var info = Probe.RunAsync(fixture).GetAwaiter().GetResult();
        Require(info.HasVideo, "fixture has video");
        Require(info.AudioTracks.Count == 2, $"fixture has 2 audio tracks (got {info.AudioTracks.Count})");
        Require(Math.Abs(info.Duration - 10) < 0.5, $"fixture duration ~10s (got {info.Duration:0.00})");
    }

    // kept segments [1,3] and [5,8] out of 10s
    private static SegmentModel MakeSegments()
    {
        var m = new SegmentModel();
        m.Reset(10);
        m.SplitAt(1); m.SplitAt(3); m.SplitAt(5); m.SplitAt(8);
        // segments now: [0,1][1,3][3,5][5,8][8,10]
        m.Delete(0);   // -> [1,3][3,5][5,8][8,10]
        m.Delete(1);   // -> [1,3][5,8][8,10]
        m.Delete(2);   // -> [1,3][5,8]
        return m;
    }

    private static void Step5LosslessExport(string fixture)
    {
        var info = Probe.RunAsync(fixture).GetAwaiter().GetResult();
        var outPath = Path.Combine(Paths.SelfTestDir, "out_lossless.mkv");
        var settings = new ExportSettings
        {
            Mode = ExportMode.Lossless,
            OutputPath = outPath,
            Tracks = info.AudioTracks.Select(t => new TrackSetting { RelIndex = t.RelIndex }).ToList(),
        };
        var (ok, err) = new ExportJob().RunAsync(info, MakeSegments(), settings, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(ok, $"lossless export succeeds{(ok ? "" : $": {err}")}");

        var outInfo = Probe.RunAsync(outPath).GetAwaiter().GetResult();
        Require(outInfo.Duration is > 3.4 and < 6.6,
            $"lossless duration ~5s within keyframe tolerance (got {outInfo.Duration:0.00})");
        Require(outInfo.AudioTracks.Count == 2, $"lossless keeps 2 audio tracks (got {outInfo.AudioTracks.Count})");
    }

    private static void Step6PreciseExport(string fixture)
    {
        var info = Probe.RunAsync(fixture).GetAwaiter().GetResult();
        var outPath = Path.Combine(Paths.SelfTestDir, "out_precise.mp4");
        var settings = new ExportSettings
        {
            Mode = ExportMode.Precise,
            ContainerExt = "mp4",
            OutputPath = outPath,
            Tracks = new List<TrackSetting>
            {
                new() { RelIndex = 0, Volume = 0.5 },
                new() { RelIndex = 1, Include = false },
            },
        };
        var (ok, err) = new ExportJob().RunAsync(info, MakeSegments(), settings, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(ok, $"precise export succeeds{(ok ? "" : $": {err}")}");

        var outInfo = Probe.RunAsync(outPath).GetAwaiter().GetResult();
        Require(Math.Abs(outInfo.Duration - 5) < 0.2,
            $"precise duration 5s frame-accurate (got {outInfo.Duration:0.00})");
        Require(outInfo.AudioTracks.Count == 1,
            $"precise drops excluded track (got {outInfo.AudioTracks.Count})");
    }
}
