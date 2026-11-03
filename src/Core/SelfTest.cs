using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Cut.Core;

// headless smoke test run by CI: cut.exe --selftest (and by tests/ on a dev mac).
// proves on real windows: tools are staged, libmpv loads through our P/Invoke surface,
// the preview audio graph and layer rendering work, and every export path produces
// correct output (durations, cut landing, mutes, overlays, size targets). exit 0 = pass.
public static class SelfTest
{
    private static string _log = "";
    private static string _dir = "";
    private static int _failures;

    // must not run on the wpf dispatcher thread: the steps block on async code,
    // and continuations posting back to a blocked dispatcher would deadlock.
    public static int Run(bool skipMpv = false) => Task.Run(() => RunCore(skipMpv)).GetAwaiter().GetResult();

    private static int RunCore(bool skipMpv)
    {
        Paths.EnsureBaseDirs();
        _log = Path.Combine(Paths.LogsDir, "selftest.log");
        File.WriteAllText(_log, "");
        _dir = Paths.SelfTestDir;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        Directory.CreateDirectory(_dir);
        var sw = Stopwatch.StartNew();
        try
        {
            Step("tools", Tools);
            var f = Step("fixtures", Fixtures);
            Step("probe", () => ProbeFixtures(f));
            Step("waveform", () => WaveformTest(f));
            Step("thumbnails", () => ThumbTest(f));
            Step("keyframe landing", () => LandingTest(f));
            Step("edits", EditsTest);
            Step("fast export", () => FastExport(f));
            Step("fast export mutes", () => FastMutes(f));
            Step("fast export mkv source", () => FastMkv(f));
            Step("exact export layers", () => ExactLayers(f));
            Step("exact export video layer", () => ExactVideoLayer(f));
            Step("exact export size target", () => ExactSize(f));
            Step("encoder detection", () =>
            {
                var e = Encoders.Best.GetAwaiter().GetResult();
                Log($"best encoder: {Encoders.Label(e)}");
            });
            if (!skipMpv)
            {
                Step("libmpv", MpvLoads);
                Step("preview audio graph", () => PreviewAudio(f));
                Step("sw render layer", () => SwRender(f));
                Step("compositor overlays", () => CompositorTest(f));
            }
        }
        catch (Exception ex)
        {
            Log($"FAIL (aborted): {ex}");
            _failures++;
        }
        Log(_failures == 0 ? $"PASS all steps in {sw.Elapsed.TotalSeconds:0.0}s" : $"FAILED {_failures} step(s)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Step(string name, Action a) => Step<object?>(name, () => { a(); return null; });

    private static T Step<T>(string name, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = f();
            Log($"[{name}] ok ({sw.ElapsedMilliseconds} ms)");
            return r;
        }
        catch (Exception ex) when (name != "fixtures" && name != "tools")
        {
            _failures++;
            Log($"[{name}] FAIL: {ex.Message}");
            return default!;
        }
    }

    private static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        File.AppendAllText(_log, line + Environment.NewLine);
        Console.WriteLine(line);
        Logger.Info($"selftest: {msg}");
    }

    private static void Require(bool cond, string what)
    {
        if (!cond) throw new InvalidOperationException(what);
        Log($"  ok: {what}");
    }

    private static string P(string name) => Path.Combine(_dir, name);

    private static void Ff(params string[] args)
    {
        var all = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        all.AddRange(args);
        var (code, err) = ExportJob.RunFfmpegAsync(all, CancellationToken.None, log: false).GetAwaiter().GetResult();
        if (code != 0) throw new InvalidOperationException($"ffmpeg failed: {err}");
    }

    private sealed record Fx(string Main, string Mkv, string Png, string Layer, string Music, MediaInfo Info);

    private static void Tools()
    {
        var missing = ToolPaths.FirstMissing();
        Require(missing == null, $"tools present beside exe{(missing != null ? $" (missing {missing})" : "")}");
        foreach (var exe in new[] { ToolPaths.Ffmpeg, ToolPaths.Ffprobe })
        {
            var text = Proc.RunTextAsync(exe, new[] { "-version" }, CancellationToken.None).GetAwaiter().GetResult();
            Require(text.Length > 0, $"{Path.GetFileName(exe)} runs: {text.Split('\n')[0].Trim()}");
        }
    }

    private static Fx Fixtures()
    {
        var main = P("main.mp4");
        var (code, err) = ExportJob.RunFfmpegAsync(ExportPlan.Fixture(main), CancellationToken.None, log: false)
            .GetAwaiter().GetResult();
        Require(code == 0 && File.Exists(main), $"main fixture{(code != 0 ? ": " + err : "")}");
        // mkv remux: the muxer writes cues per cluster, a sparser seek index than mp4
        var mkv = P("main.mkv");
        Ff("-i", main, "-map", "0", "-c", "copy", mkv);
        var png = P("sticker.png");
        Ff("-f", "lavfi", "-i", "color=c=0x00FF00:s=160x90,format=rgba", "-frames:v", "1", png);
        var layer = P("layer.mp4");
        Ff("-f", "lavfi", "-i", "color=c=0x0000FF:s=320x180:r=30:d=3", "-f", "lavfi", "-i", "sine=frequency=300:duration=3",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", layer);
        var music = P("music.m4a");
        Ff("-f", "lavfi", "-i", "sine=frequency=660:duration=3", "-c:a", "aac", music);
        var info = Probe.RunAsync(main).GetAwaiter().GetResult();
        return new Fx(main, mkv, png, layer, music, info);
    }

    private static void ProbeFixtures(Fx f)
    {
        var info = f.Info;
        Require(info.HasVideo && Math.Abs(info.Fps - 30) < 0.1, $"main has 30 fps video (got {info.Fps:0.##})");
        Require(info.AudioTracks.Count == 2, $"main has 2 audio tracks (got {info.AudioTracks.Count})");
        Require(info.AudioTracks[0].ShortLabel == "game", $"track titles read (got {info.AudioTracks[0].ShortLabel})");
        Require(Math.Abs(info.Duration - 10) < 0.2, $"main duration ~10s (got {info.Duration:0.00})");
        Require(Probe.RunAsync(f.Png).GetAwaiter().GetResult().GuessClipKind() == ClipKind.Image, "png probes as image");
        var layer = Probe.RunAsync(f.Layer).GetAwaiter().GetResult();
        Require(layer.GuessClipKind() == ClipKind.Video && layer.AudioTracks.Count == 1, "layer probes as video with sound");
        Require(Probe.RunAsync(f.Music).GetAwaiter().GetResult().GuessClipKind() == ClipKind.Audio, "m4a probes as audio");
    }

    private static void WaveformTest(Fx f)
    {
        var w = Waveform.Start(f.Main, 0, 10, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        while (!w.Done && sw.ElapsedMilliseconds < 20000) Thread.Sleep(50);
        Require(w.Done, "waveform finished");
        Require(Math.Abs(w.Count - 1000) < 30, $"100 buckets per second (got {w.Count})");
        var peak = w.Peak(2, 3);
        Require(peak > 100, $"sine shows as loud (peak {peak})");
    }

    private static void ThumbTest(Fx f)
    {
        using var th = new Thumbnails(f.Info, 40);
        th.Request(5);
        var sw = Stopwatch.StartNew();
        while (th.Get(5) == null && sw.ElapsedMilliseconds < 15000) Thread.Sleep(30);
        var t = th.Get(5);
        Require(t != null && t.Bgra.Length == t.Width * t.Height * 4, $"thumbnail decoded ({th.Width}x{th.Height})");
    }

    private static void LandingTest(Fx f)
    {
        var k = new Keyframes(f.Info);
        double L(double t) => k.StartAsync(t).GetAwaiter().GetResult().Landing;
        Require(Math.Abs(L(2.5) - 2.0) < 0.05, $"landing(2.5) = 2.0 (got {L(2.5):0.000})");
        Require(Math.Abs(L(3.0) - 3.0) < 0.05, $"landing on a keyframe stays (got {L(3.0):0.000})");
        Require(L(0.4) < 0.05, $"landing(0.4) = 0 (got {L(0.4):0.000})");
        var ks = k.InRange(0, 9.9);
        var sw = Stopwatch.StartNew();
        while (ks.Count == 0 && sw.ElapsedMilliseconds < 10000) { Thread.Sleep(50); ks = k.InRange(0, 9.9); }
        Require(ks.Count >= 9, $"keyframes in range listed (got {ks.Count})");
    }

    private static void EditsTest()
    {
        var s = new ProjectState { Segments = ImmutableArray.Create(new Segment(0, 10)) };
        s = Edits.CutRange(s, 2, 4);
        Require(s.Segments.Length == 2 && s.Segments[0].End == 2 && s.Segments[1].Start == 4, "cut range splits");
        s = Edits.CutRange(s, 1, 5);
        Require(s.Segments.Length == 2 && s.Segments[0].End == 1 && s.Segments[1].Start == 5, "cut range across a gap");
        var map = new TimeMap(s.Segments);
        Require(Math.Abs(map.Total - 6) < 1e-9 && Math.Abs(map.ToOutput(6) - 2) < 1e-9, "time map source to output");
        var pieces = map.Pieces(0.5, 5);
        Require(pieces.Count == 2 && Math.Abs(pieces[1].OutStart - 1) < 1e-9 && Math.Abs(pieces[1].Duration - 0.5) < 1e-9,
            "clips slice through cuts");
        var muted = Edits.Mute(new ProjectState { AudioLanes = ImmutableArray.Create(new Lane { Id = 1, Kind = LaneKind.Audio }) }, 1, 1, 2);
        muted = Edits.Mute(muted, 1, 1.5, 3);
        Require(muted.AudioLanes[0].Mutes.Length == 1 && muted.AudioLanes[0].Mutes[0].End == 3, "mutes merge");
        var (eff, skipped) = Keyframes.Effective(new[] { new Segment(0, 3), new Segment(3.5, 6) },
            t => new StartPoint(Math.Floor(t / 2) * 2, t));
        Require(eff.Count == 1 && skipped == 1, "fast mode merges a cut shorter than the keyframe gap");
    }

    private static ProjectState Base(MediaInfo info, params Segment[] segs)
        => ProjectState.Fresh(info, 10) with { Segments = segs.ToImmutableArray() };

    private static (bool Ok, string? Err, ExportPlan? Plan) Export(MediaInfo info, ProjectState s, ExportOptions o)
    {
        var job = new ExportJob();
        var (ok, err) = job.RunAsync(info, s, o, CancellationToken.None).GetAwaiter().GetResult();
        return (ok, err, job.LastPlan);
    }

    private static void FastExport(Fx f)
    {
        var k = new Keyframes(f.Info);
        var s = Base(f.Info, new Segment(1.5, 3), new Segment(5, 8));
        var outPath = P("fast.mp4");
        var (ok, err, plan) = Export(f.Info, s, new ExportOptions
        {
            Mode = ExportMode.Fast, Audio = AudioMode.Separate, ContainerExt = "mp4", OutputPath = outPath,
            Keys = k,
        });
        Require(ok, $"fast export succeeds{(ok ? "" : ": " + err)}");
        var o = Probe.RunAsync(outPath).GetAwaiter().GetResult();
        Require(Math.Abs(plan!.OutputDuration - 5) < 0.05, $"planned duration 5s with start snapped to 1.0 (got {plan.OutputDuration:0.00})");
        Require(Math.Abs(o.Duration - 5) < 0.2, $"fast duration ~5s (got {o.Duration:0.00})");
        Require(o.AudioTracks.Count == 2, $"separate keeps 2 audio tracks (got {o.AudioTracks.Count})");
        Require(o.AudioTracks[0].Codec == "aac", "untouched tracks are copied");

        var mixPath = P("fast_mix.mp4");
        (ok, err, _) = Export(f.Info, s, new ExportOptions
        {
            Mode = ExportMode.Fast, Audio = AudioMode.Mix, ContainerExt = "mp4", OutputPath = mixPath,
            Keys = k,
        });
        Require(ok, $"fast mix export succeeds{(ok ? "" : ": " + err)}");
        var m = Probe.RunAsync(mixPath).GetAwaiter().GetResult();
        Require(m.AudioTracks.Count == 1 && Math.Abs(m.Duration - 5) < 0.2, $"mix gives one track, ~5s (got {m.AudioTracks.Count}, {m.Duration:0.00})");

        // many joins: video frame count must match the plan exactly, audio must not drift
        var many = Base(f.Info, new Segment(0.2, 1.1), new Segment(1.5, 2.3), new Segment(3.2, 4.4),
            new Segment(5.1, 6.6), new Segment(7.3, 8.0), new Segment(8.5, 9.6));
        var manyPath = P("fast_many.mp4");
        (ok, err, plan) = Export(f.Info, many, new ExportOptions
        {
            Mode = ExportMode.Fast, Audio = AudioMode.Mix, ContainerExt = "mp4", OutputPath = manyPath, Keys = k,
        });
        Require(ok, $"fast export with 6 segments succeeds{(ok ? "" : ": " + err)}");
        var frames = CountFrames(manyPath);
        var expectFrames = (int)Math.Round(plan!.OutputDuration * 30);
        // b-frames decoded after the next reference frame can't be kept by a copy cut: at
        // most the reorder depth (2) is lost per join, never an extra frame added
        var joins = plan.Runs.Count - 1;
        Require(frames <= expectFrames && expectFrames - frames <= 2 * joins,
            $"frame count matches the plan within b-frame reorder ({frames} vs {expectFrames}, {joins} segments)");
        var (vd, ad) = StreamDurations(manyPath);
        Require(Math.Abs(ad - vd) < 0.12 && Math.Abs(ad - plan.OutputDuration) < 0.08, $"audio stays with video across joins (v {vd:0.000}, a {ad:0.000})");
    }

    private static int CountFrames(string file)
    {
        var t = Proc.RunTextAsync(ToolPaths.Ffprobe, new[]
        {
            "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets",
            "-of", "csv=p=0", file,
        }, CancellationToken.None).GetAwaiter().GetResult();
        return int.TryParse(t.Trim().TrimEnd(','), out var n) ? n : -1;
    }

    // end time of the last packet per stream type, which works for containers without stream durations
    private static (double Video, double Audio) StreamDurations(string file)
    {
        double Last(string sel)
        {
            var t = Proc.RunTextAsync(ToolPaths.Ffprobe, new[]
            {
                "-v", "error", "-select_streams", sel, "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", file,
            }, CancellationToken.None).GetAwaiter().GetResult();
            double end = 0;
            foreach (var line in t.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split(',');
                if (p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                    && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    end = Math.Max(end, pts + d);
            }
            return end;
        }
        return (Last("v:0"), Last("a:0"));
    }

    private static void FastMutes(Fx f)
    {
        var k = new Keyframes(f.Info);
        var s = Base(f.Info, new Segment(1, 3), new Segment(5, 8));
        s = Edits.Mute(s, s.AudioLanes[0].Id, 5.5, 6.5); // output 2.5..3.5
        var outPath = P("fast_mute.mkv");
        var (ok, err, _) = Export(f.Info, s, new ExportOptions
        {
            Mode = ExportMode.Fast, Audio = AudioMode.Separate, ContainerExt = "mkv", OutputPath = outPath,
            Keys = k,
        });
        Require(ok, $"fast export with mute succeeds{(ok ? "" : ": " + err)}");
        var quiet = MaxVolume(outPath, 0, 2.65, 0.7);
        var loud = MaxVolume(outPath, 0, 0.3, 0.7);
        var other = MaxVolume(outPath, 1, 2.65, 0.7);
        Require(quiet < -60, $"muted part is silent ({quiet:0.0} dB)");
        Require(loud > -30, $"unmuted part is loud ({loud:0.0} dB)");
        Require(other > -30, $"other track untouched by the mute ({other:0.0} dB)");
    }

    private static void FastMkv(Fx f)
    {
        var info = Probe.RunAsync(f.Mkv).GetAwaiter().GetResult();
        var k = new Keyframes(info);
        var s = Base(info, new Segment(2.5, 4), new Segment(6.5, 9));
        double L(double t) => k.StartAsync(t).GetAwaiter().GetResult().Landing;
        var expected = (4 - L(2.5)) + (9 - L(6.5));
        var outPath = P("fast_from_mkv.mkv");
        var (ok, err, plan) = Export(info, s, new ExportOptions
        {
            Mode = ExportMode.Fast, Audio = AudioMode.Separate, ContainerExt = "mkv", OutputPath = outPath, Keys = k,
        });
        Require(ok, $"fast export from mkv succeeds{(ok ? "" : ": " + err)}");
        var o = Probe.RunAsync(outPath).GetAwaiter().GetResult();
        Log($"  mkv landings: {L(2.5):0.000}, {L(6.5):0.000}; planned {plan!.OutputDuration:0.00}");
        Require(Math.Abs(o.Duration - expected) < 0.25, $"duration matches predicted landing ({o.Duration:0.00} vs {expected:0.00})");
    }

    private static void ExactLayers(Fx f)
    {
        var png = Probe.RunAsync(f.Png).GetAwaiter().GetResult();
        var music = Probe.RunAsync(f.Music).GetAwaiter().GetResult();
        var s = Base(f.Info, new Segment(1, 3), new Segment(5, 8));
        s = Edits.UpdateLane(s, s.AudioLanes[0].Id, l => l with { Volume = 0.5 });
        s = Edits.UpdateLane(s, s.AudioLanes[1].Id, l => l with { Enabled = false });
        s = Edits.AddClip(s, new Clip
        {
            Kind = ClipKind.Image, Path = f.Png, Start = 5.5, Duration = 2, MediaWidth = png.Width, MediaHeight = png.Height,
            X = 0.5, Y = 0.5, W = 0.25,
        }, null, out _);
        s = Edits.AddClip(s, new Clip
        {
            Kind = ClipKind.Audio, Path = f.Music, Start = 1.5, Duration = 1, MediaDuration = music.Duration, HasAudio = true,
        }, null, out _);
        Require(s.ClipLanes.Length == 1, "image and music share a free lane");
        var outPath = P("exact_layers.mp4");
        var (ok, err, plan) = Export(f.Info, s, new ExportOptions
        {
            Mode = ExportMode.Exact, ContainerExt = "mp4", OutputPath = outPath, Encoder = VideoEncoder.X264,
        });
        Require(ok, $"exact export with layers succeeds{(ok ? "" : ": " + err)}");
        var o = Probe.RunAsync(outPath).GetAwaiter().GetResult();
        Require(Math.Abs(o.Duration - 5) < 0.1, $"exact duration 5s frame-accurate (got {o.Duration:0.00})");
        Require(o.AudioTracks.Count == 1, $"mixed to one audio track (got {o.AudioTracks.Count})");
        var (cx, cy) = (o.Width / 2, o.Height / 2);
        var inside = Pixel(outPath, 3.0, cx, cy);
        var outside = Pixel(outPath, 1.2, cx, cy);
        Require(IsColor(inside, 0, 255, 0), $"sticker visible at output 3.0s (rgb {Rgb(inside)})");
        Require(!IsColor(outside, 0, 255, 0), $"sticker hidden before its start (rgb {Rgb(outside)})");
        var musicVol = MaxVolume(outPath, 0, 0.55, 0.4);
        Require(musicVol > -30, $"music audible at output 0.5..1.0 ({musicVol:0.0} dB)");
    }

    private static void ExactVideoLayer(Fx f)
    {
        var layer = Probe.RunAsync(f.Layer).GetAwaiter().GetResult();
        var s = Base(f.Info, new Segment(1, 3), new Segment(5, 8));
        // spans the cut at 3..5: piece [2.5,3] lands at output 1.5..2, [5,5.5] at 2..2.5
        s = Edits.AddClip(s, new Clip
        {
            Kind = ClipKind.Video, Path = f.Layer, Start = 2.5, Duration = 3, In = 0, MediaDuration = 3,
            MediaWidth = layer.Width, MediaHeight = layer.Height, HasAudio = true, X = 0.2, Y = 0.2, W = 0.3, Opacity = 1,
        }, null, out _);
        var outPath = P("exact_video_layer.mkv");
        var (ok, err, _) = Export(f.Info, s, new ExportOptions
        {
            Mode = ExportMode.Exact, Audio = AudioMode.Separate, ContainerExt = "mkv", OutputPath = outPath,
            Encoder = VideoEncoder.X264, Height = 360,
        });
        Require(ok, $"exact export with video layer succeeds{(ok ? "" : ": " + err)}");
        var o = Probe.RunAsync(outPath).GetAwaiter().GetResult();
        Require(o.AudioTracks.Count == 2, $"separate keeps lanes as tracks (got {o.AudioTracks.Count})");
        var px = (int)(o.Width * 0.2);
        var py = (int)(o.Height * 0.2);
        Require(IsColor(Pixel(outPath, 1.7, px, py), 0, 0, 255), "video layer visible in its first piece");
        Require(IsColor(Pixel(outPath, 2.2, px, py), 0, 0, 255), "video layer continues after the cut");
        Require(!IsColor(Pixel(outPath, 1.2, px, py), 0, 0, 255), "video layer hidden before it starts");
        Require(!IsColor(Pixel(outPath, 2.8, px, py), 0, 0, 255), "video layer gone after it ends");
    }

    private static void ExactSize(Fx f)
    {
        var s = Base(f.Info, new Segment(0, 10));
        var outPath = P("exact_size.mp4");
        const double target = 600_000;
        var (ok, err, plan) = Export(f.Info, s, new ExportOptions
        {
            Mode = ExportMode.Exact, ContainerExt = "mp4", OutputPath = outPath, TargetBytes = target,
            Encoder = VideoEncoder.X264,
        });
        Require(ok, $"size-targeted export succeeds{(ok ? "" : ": " + err)}");
        var size = new FileInfo(outPath).Length;
        var est = plan!.Quality!.EstimatedBytes;
        Log($"  plan {plan.Quality.Width}x{plan.Quality.Height}@{plan.Quality.Fps:0.##} v={plan.Quality.VideoBitrate} a={plan.Quality.AudioBitrate}");
        Require(Math.Abs(size - est) / est < 0.25, $"size {SizeFmt.Bytes(size)} within 25% of estimate {SizeFmt.Bytes(est)}");

        var small = Quality.Plan(f.Info, 60, Quality.DiscordBytes, 1, 0, 0);
        Require(small.EstimatedBytes <= Quality.DiscordBytes * 1.001, $"discord preset fits ({SizeFmt.Bytes(small.EstimatedBytes)})");
    }

    // ---- mpv ----

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    private static void MpvLoads()
    {
        using var mpv = new MpvPlayer(MpvRole.Headless);
        var version = mpv.GetPropertyString("mpv-version");
        Require(!string.IsNullOrEmpty(version), $"libmpv loads: {version}");
    }

    private static void PreviewAudio(Fx f)
    {
        using var mpv = new MpvPlayer(MpvRole.Headless);
        mpv.Load(f.Main);
        Require(WaitFor(() => mpv.Loaded, 8000), "main fixture loads in mpv");
        var s = Base(f.Info, new Segment(0, 10));
        s = Edits.UpdateLane(s, s.AudioLanes[1].Id, l => l with { Volume = 0.4 });
        s = Edits.Mute(s, s.AudioLanes[0].Id, 1, 2);
        var graph = PreviewGraph.Audio(s, new HashSet<int>());
        Log($"  graph: {graph}");
        Require(mpv.SetProperty("lavfi-complex", graph), $"mpv accepts the mixed audio graph ({mpv.LastError})");
        mpv.SetPause(false);
        Require(WaitFor(() => mpv.TimePos > 0.8, 8000), $"plays through the graph (t={mpv.TimePos:0.00})");
        Require(mpv.SetProperty("lavfi-complex", PreviewGraph.Audio(s, new HashSet<int> { s.AudioLanes[0].Id })), "solo graph accepted");
        var t0 = mpv.TimePos;
        Require(WaitFor(() => mpv.TimePos > t0 + 0.3, 8000), "keeps playing after a graph change");
        mpv.SetPause(true);
    }

    private static void SwRender(Fx f)
    {
        using var mpv = new MpvPlayer(MpvRole.Render);
        using var r = new MpvSwRenderer(mpv);
        byte[]? got = null;
        r.Frame += (ptr, w, h, stride) =>
        {
            var row = new byte[w * 4];
            System.Runtime.InteropServices.Marshal.Copy(ptr + h / 2 * stride, row, 0, row.Length);
            got = row;
        };
        r.SetTarget(64, 36, 1);
        mpv.SetProperty("aid", "no");
        mpv.Load(f.Layer);
        Require(WaitFor(() => got != null, 8000), $"sw render produces frames ({r.FramesRendered})");
        var mid = got!.AsSpan(32 * 4, 4);
        Require(mid[0] > 180 && mid[1] < 80 && mid[2] < 80 && mid[3] == 255, $"blue layer rendered opaque (bgra {mid[0]},{mid[1]},{mid[2]},{mid[3]})");
    }

    // renders the "preview" through the sw api so we can look at what the compositor drew
    private static void CompositorTest(Fx f)
    {
        using var main = new MpvPlayer(MpvRole.Render);
        using var r = new MpvSwRenderer(main);
        byte[]? frame = null;
        int fw = 320, fh = 180, stride = 0;
        r.Frame += (ptr, w, h, st) =>
        {
            var buf = new byte[st * h];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, buf.Length);
            stride = st;
            frame = buf;
        };
        r.SetTarget(fw, fh, 1);
        main.SetProperty("aid", "no");
        main.Load(f.Main);
        Require(WaitFor(() => main.Loaded && frame != null, 8000), "preview stand-in renders");

        var png = Probe.RunAsync(f.Png).GetAwaiter().GetResult();
        var s = Base(f.Info, new Segment(0, 10));
        s = Edits.AddClip(s, new Clip
        {
            Kind = ClipKind.Image, Path = f.Png, Start = 0, Duration = 5, MediaWidth = png.Width, MediaHeight = png.Height,
            X = 0.75, Y = 0.25, W = 0.25,
        }, null, out _);
        using var comp = new PreviewCompositor(main, a => a());
        comp.SetVideoRect((0, 0, fw, fh));
        comp.SetState(s, new HashSet<int>());
        comp.Tick(0.5, false, true);
        // image decode is async; tick until it lands, then force a fresh render
        var ok = WaitFor(() =>
        {
            comp.Tick(0.5, false, false);
            r.Invalidate();
            Thread.Sleep(60);
            var fr = frame;
            if (fr == null) return false;
            int x = (int)(fw * 0.75), y = (int)(fh * 0.25);
            var i = y * stride + x * 4;
            return fr[i] < 80 && fr[i + 1] > 180 && fr[i + 2] < 80;
        }, 8000);
        Require(ok, "image layer composited over the preview at its placement");
    }

    // ---- probes ----

    private static double MaxVolume(string file, int track, double start, double dur)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-ss", start.ToString(CultureInfo.InvariantCulture),
            "-t", dur.ToString(CultureInfo.InvariantCulture), "-i", file, "-map", $"0:a:{track}",
            "-af", "volumedetect", "-f", "null", "-",
        };
        var (_, err) = ExportJob.RunFfmpegAsync(args, CancellationToken.None, log: false).GetAwaiter().GetResult();
        var m = Regex.Match(err, @"max_volume:\s*(-?[\d.]+|-inf) dB");
        if (!m.Success) return -200;
        return m.Groups[1].Value == "-inf" ? -200 : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static byte[] Pixel(string file, double t, int x, int y)
    {
        var px = Proc.RunBytesAsync(ToolPaths.Ffmpeg, new[]
        {
            "-v", "error", "-nostdin", "-ss", t.ToString(CultureInfo.InvariantCulture), "-i", file,
            "-frames:v", "1", "-vf", $"crop=2:2:{Math.Max(0, x - 1)}:{Math.Max(0, y - 1)}",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
        }, CancellationToken.None).GetAwaiter().GetResult();
        return px.Length >= 3 ? px[..3] : new byte[3];
    }

    private static bool IsColor(byte[] rgb, int r, int g, int b)
        => Math.Abs(rgb[0] - r) < 60 && Math.Abs(rgb[1] - g) < 60 && Math.Abs(rgb[2] - b) < 60;

    private static string Rgb(byte[] p) => $"{p[0]},{p[1]},{p[2]}";
}
