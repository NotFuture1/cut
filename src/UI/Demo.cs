using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;

namespace Cut.UI;

// cut.exe --demo <dir>: a scripted walkthrough driven with real mouse and keyboard input.
// it doubles as an end-to-end ui test (each step checks the project state it should
// produce) and records the screen with ffmpeg, then cuts the recording into the gifs
// and stills used by docs/GUIDE.md. runs on the ci windows runner.
public sealed class Demo
{
    public static bool Active { get; private set; }
    private static Demo? _current;

    private readonly MainWindow _w;
    private readonly string _out;
    private readonly Stopwatch _clock = new();
    private readonly List<(string Name, double Start, double End)> _clips = new();
    private readonly List<(string Name, double At)> _stills = new();
    private readonly List<(string Step, bool Ok, string Note)> _results = new();
    private Process? _rec;
    private Point _cursor;
    private double _markStart;

    public Demo(MainWindow w, string outDir)
    {
        _w = w;
        _out = Path.GetFullPath(outDir);
    }

    public static void Fail(Exception ex) => _current?.Check(false, "unhandled", ex.Message);

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Logger.Info("demo: " + msg);
        File.AppendAllText(Path.Combine(_out, "demo.log"), line + Environment.NewLine);
    }

    private void Check(bool ok, string step, string note)
    {
        _results.Add((step, ok, note));
        Log($"{(ok ? "ok  " : "FAIL")} [{step}] {note}");
    }

    private double Now => _clock.Elapsed.TotalSeconds;
    private void Begin() => _markStart = Now;
    private void End(string name) => _clips.Add((name, Math.Max(0, _markStart - 0.3), Now + 0.5));
    private void Still(string name) => _stills.Add((name, Now));

    public async Task RunAsync()
    {
        Active = true;
        _current = this;
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "demo.log"), "");
        try
        {
            Log("making assets");
            var (video, sticker, music) = await MakeAssets();
            PlaceWindow();
            await Task.Delay(1200);
            StartRecording();
            await Task.Delay(1800);
            await Steps(video, sticker, music);
            await Task.Delay(800);
            await StopRecording();
            await MakeMedia();
        }
        catch (Exception ex)
        {
            Check(false, "demo", ex.ToString());
            await StopRecording();
        }
        finally
        {
            var fails = _results.Count(r => !r.Ok);
            File.WriteAllText(Path.Combine(_out, "demo.json"), JsonSerializer.Serialize(new
            {
                passed = fails == 0,
                steps = _results.Select(r => new { r.Step, r.Ok, r.Note }),
                clips = _clips.Select(c => new { c.Name, c.Start, c.End }),
                stills = _stills.Select(s => new { s.Name, s.At }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Log(fails == 0 ? "PASS" : $"FAILED {fails}");
            Application.Current.Shutdown(fails == 0 ? 0 : 1);
        }
    }

    // ---- input helpers (physical screen pixels) ----

    private Point Scr(FrameworkElement el, double x, double y) => el.PointToScreen(new Point(x, y));
    private Point Center(FrameworkElement el) => Scr(el, el.ActualWidth / 2, el.ActualHeight / 2);

    private async Task MoveTo(Point to, int ms = 420)
    {
        var from = _cursor;
        var steps = Math.Max(1, ms / 15);
        for (int i = 1; i <= steps; i++)
        {
            var k = (double)i / steps;
            var e = k < 0.5 ? 2 * k * k : 1 - Math.Pow(-2 * k + 2, 2) / 2; // ease in-out
            Native.SetCursorPos((int)Math.Round(from.X + (to.X - from.X) * e), (int)Math.Round(from.Y + (to.Y - from.Y) * e));
            await Task.Delay(15);
        }
        _cursor = to;
    }

    private async Task Click(Point p, int moveMs = 420)
    {
        await MoveTo(p, moveMs);
        await Task.Delay(120);
        Native.MouseButton(true, true);
        await Task.Delay(70);
        Native.MouseButton(true, false);
        await Task.Delay(250);
    }

    private async Task Drag(Point a, Point b, int ms = 900)
    {
        await MoveTo(a);
        await Task.Delay(200);
        Native.MouseButton(true, true);
        await Task.Delay(140);
        await MoveTo(b, ms);
        await Task.Delay(220);
        Native.MouseButton(true, false);
        await Task.Delay(300);
    }

    private static async Task Key(int vk, bool ctrl = false)
    {
        if (ctrl) Native.Key(0x11, true);
        Native.Key(vk, true);
        await Task.Delay(50);
        Native.Key(vk, false);
        if (ctrl) Native.Key(0x11, false);
        await Task.Delay(250);
    }

    private static async Task Until(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(50);
    }

    private TimelineView Tl => _w.Timeline;
    private ProjectState S => _w.Store.State;
    private Point AtTime(double t, Rect lane) => Scr(Tl, Tl.TimeToX(t), lane.Y + lane.Height / 2);

    // ---- the walkthrough ----

    private async Task Steps(string video, string sticker, string music)
    {
        _cursor = Center(_w);
        Native.SetCursorPos((int)_cursor.X, (int)_cursor.Y);

        // 1. open
        Begin();
        await MoveTo(Center(_w.EmptyState));
        await Task.Delay(700);
        _w.OpenFile(video);
        await Until(() => _w.Media != null && S.Segments.Length == 1, 15000);
        await Task.Delay(2500); // let waveforms and thumbnails fill in
        Check(_w.Media != null && S.AudioLanes.Length == 2, "open", $"opened, {S.AudioLanes.Length} audio lanes");
        Still("overview-start");
        End("01-open");
        var dur = _w.Duration;

        // 2. trim both ends by dragging the edges
        Begin();
        var vl = Tl.VideoLaneRect();
        await Drag(AtTime(0, vl) + new Vector(2, 0), AtTime(3.0, vl), 1000);
        await MoveTo(AtTime(S.Segments[0].Start, vl) + new Vector(0, -4), 200);
        await Task.Delay(700);
        Check(Math.Abs(S.Segments[0].Start - 3.0) < 0.15, "trim start", $"start {S.Segments[0].Start:0.000}");
        await Drag(AtTime(dur, vl) + new Vector(-2, 0), AtTime(dur - 3.5, vl), 1000);
        await Task.Delay(500);
        Check(Math.Abs(S.Segments[^1].End - (dur - 3.5)) < 0.2, "trim end", $"end {S.Segments[^1].End:0.000}");
        End("02-trim");

        // 3. mark a part on the video lane and cut it out
        Begin();
        vl = Tl.VideoLaneRect();
        await Drag(AtTime(10.4, vl), AtTime(14.6, vl), 900);
        await Task.Delay(600);
        await Key(0x58); // x
        await Task.Delay(900);
        Check(S.Segments.Length == 2 && Math.Abs(S.Segments[0].End - 10.4) < 0.15, "cut range",
            string.Join(" ", S.Segments.Select(x => $"[{x.Start:0.00},{x.End:0.00}]")));
        Still("cut");
        End("03-cut");

        // 4. mute part of one track
        Begin();
        var mic = S.AudioLanes.First(l => l.Name == "mic");
        var ml = Tl.LaneRect(mic.Id);
        await Drag(AtTime(17.8, ml), AtTime(21.6, ml), 900);
        await Task.Delay(600);
        await Key(0x4D); // m
        await Task.Delay(1000);
        Check(S.FindLane(mic.Id)?.Mutes.Length == 1, "mute part", $"mic mutes {S.FindLane(mic.Id)?.Mutes.Length}");
        Still("mute");
        End("04-mute");

        // 5. tracks: turn one down, move another above it
        Begin();
        var game = S.AudioLanes.First(l => l.Name == "game");
        var vb = Tl.VolumeBar(game.Id);
        await Drag(Scr(Tl, vb.X + vb.Width * 0.5, vb.Y + vb.Height / 2), Scr(Tl, vb.X + vb.Width * 0.35, vb.Y + vb.Height / 2), 700);
        Check(Math.Abs((S.FindLane(game.Id)?.Volume ?? 1) - 0.7) < 0.08, "lane volume", $"game {S.FindLane(game.Id)?.Volume:0.00}");
        var micHeader = Tl.HeaderRect(mic.Id);
        var gameHeader = Tl.HeaderRect(game.Id);
        await Drag(Scr(Tl, 10, micHeader.Y + micHeader.Height / 2), Scr(Tl, 10, gameHeader.Y + 6), 800);
        await Task.Delay(500);
        Check(S.AudioLanes[0].Name == "mic", "reorder lanes", $"order {string.Join(",", S.AudioLanes.Select(l => l.Name))}");
        End("05-tracks");

        // 6. fast mode: see where stream-copy cuts land, zoom in on one
        Begin();
        await Click(Center(_w.FastModeRadio));
        await Until(() => _w.Keys?.CachedLanding(S.Segments[0].Start) != null, 8000);
        await Task.Delay(900);
        Check(_w.Keys?.CachedLanding(S.Segments[0].Start) is double l && l < S.Segments[0].Start - 0.5, "fast landing",
            $"start {S.Segments[0].Start:0.00} lands {_w.Keys?.CachedLanding(S.Segments[0].Start):0.00}");
        vl = Tl.VideoLaneRect();
        await MoveTo(AtTime(S.Segments[0].Start, vl));
        for (int i = 0; i < 6; i++)
        {
            Native.Key(0x11, true);
            Native.Wheel(120);
            Native.Key(0x11, false);
            await Task.Delay(160);
        }
        await Task.Delay(1200);
        Still("fast-zoom");
        await Click(Center(_w.ExactMode));
        await Task.Delay(500);
        Tl.Fit();
        await Task.Delay(500);
        End("06-fast");

        // 7. a sticker over the video: add it, drag it into the corner, make it bigger
        Begin();
        await MoveTo(Center(_w.AddBtn));
        await Task.Delay(500);
        await _w.AddMediaFiles(new[] { sticker }, 4.2, null);
        var clipId = S.AllClips.FirstOrDefault()?.Id ?? 0;
        Check(clipId != 0, "add layer", $"{S.AllClips.Count()} clips");
        var ruler = new Rect(0, 0, Tl.ActualWidth, TimelineView.RulerH);
        await Click(AtTime(6.0, ruler));
        await Task.Delay(1400);
        if (_w.VideoRect is { } r && S.FindClip(clipId) is { } fc)
        {
            var dpi = VisualTreeHelper.GetDpi(_w).PixelsPerDip;
            Point OnVideo(double nx, double ny) => _w.VideoArea.PointToScreen(new Point((r.X + nx * r.W) / dpi, (r.Y + ny * r.H) / dpi));
            await Drag(OnVideo(fc.Clip.X, fc.Clip.Y), OnVideo(0.74, 0.22), 1000);
            await Task.Delay(500);
            var moved = S.FindClip(clipId)!.Value.Clip;
            Check(moved.X > 0.65 && moved.Y < 0.32, "drag layer on video", $"x {moved.X:0.00} y {moved.Y:0.00}");
            var aspect = _w.Media?.Aspect ?? 16.0 / 9;
            var corner = OnVideo(moved.X + moved.W / 2, moved.Y + moved.HeightFrac(aspect) / 2) + new Vector(-3, -3);
            await Drag(corner, corner + new Vector(70, 30), 700);
            await Task.Delay(600);
            Check(S.FindClip(clipId)!.Value.Clip.W > moved.W + 0.02, "resize layer", $"w {moved.W:0.00} -> {S.FindClip(clipId)!.Value.Clip.W:0.00}");
        }
        else Check(false, "drag layer on video", "no video rect");
        Still("layer");
        End("07-layer");

        // 8. music on its own lane, slid along the timeline
        Begin();
        await MoveTo(Center(_w.AddBtn));
        await Task.Delay(400);
        await _w.AddMediaFiles(new[] { music }, 16.0, null);
        await Task.Delay(1200);
        var song = S.AllClips.FirstOrDefault(c => c.Kind == ClipKind.Audio);
        Check(song != null, "add music", song != null ? $"at {song.Start:0.00}" : "missing");
        if (song != null && S.FindClip(song.Id) is { } sf)
        {
            var lr = Tl.LaneRect(sf.Lane.Id);
            await Drag(AtTime(song.Start + song.Duration / 2, lr), AtTime(song.Start + song.Duration / 2 - 3, lr), 900);
            await Task.Delay(600);
            var after = S.FindClip(song.Id)!.Value.Clip.Start;
            Check(Math.Abs(after - (song.Start - 3)) < 0.3, "move music", $"{song.Start:0.00} -> {after:0.00}");
        }
        End("08-music");

        // 9. play: the preview hops over the cut and plays the mix
        Begin();
        await Click(AtTime(8.6, ruler));
        await Key(0x20); // space
        await Task.Delay(3800);
        await Key(0x20);
        Check(_w.Playhead > 14.4, "skip cuts", $"playhead {_w.Playhead:0.00}");
        End("09-play");

        // 10. export for discord
        Begin();
        var dlg = _w.OpenExport(modal: false);
        if (dlg == null) { Check(false, "export", "no dialog"); return; }
        await Task.Delay(900);
        Still("export-dialog-original");
        var discord = dlg.Presets.Children.OfType<Button>().First(b => b.Tag as string == "discord");
        await Click(Center(discord));
        await Task.Delay(1100);
        Still("export-dialog");
        var outPath = dlg.OutBox.Text;
        await Click(Center(dlg.ExportBtn));
        await Until(() => dlg.RunStatus.Text is "done" or "export failed", 180000);
        await Task.Delay(1800);
        var exported = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
        Check(dlg.RunStatus.Text == "done" && exported > 0 && exported <= 10_000_000, "export",
            $"{dlg.RunStatus.Text}, {SizeFmt.Bytes(exported)} {dlg.ErrorBox.Text}");
        Still("export-done");
        End("10-export");
        dlg.Close();
        if (exported > 0)
        {
            var info = await Probe.RunAsync(outPath);
            var expect = S.KeptDuration;
            Check(Math.Abs(info.Duration - expect) < 0.2 && info.AudioTracks.Count == 1, "export result",
                $"{info.Duration:0.00}s (expect {expect:0.00}), {info.Width}x{info.Height}, {info.AudioTracks.Count} audio");
            File.Copy(outPath, Path.Combine(_out, "exported.mp4"), true);
        }
        await Task.Delay(600);
        Still("final");
    }

    // ---- window, assets, recording ----

    private void PlaceWindow()
    {
        var sw = SystemParameters.WorkArea;
        _w.WindowState = WindowState.Normal;
        _w.Left = sw.Left;
        _w.Top = sw.Top;
        _w.Width = Math.Min(1440, sw.Width);
        _w.Height = Math.Min(900, sw.Height);
        _w.Activate();
        Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(_w).Handle);
        Log($"work area {sw.Width}x{sw.Height}, window {_w.Width}x{_w.Height}");
    }

    private async Task<(string Video, string Sticker, string Music)> MakeAssets()
    {
        var dir = Path.Combine(_out, "assets");
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "gameplay.mp4");
        var music = Path.Combine(dir, "song.m4a");
        var sticker = Path.Combine(dir, "gg.png");

        string font = File.Exists(@"C:\Windows\Fonts\segoeuib.ttf") ? "C\\:/Windows/Fonts/segoeuib.ttf" : "";
        var clock = font.Length > 0
            ? $",drawtext=fontfile='{font}':text='%{{pts\\:hms}}':x=40:y=36:fontsize=34:fontcolor=white@0.9:box=1:boxcolor=black@0.45:boxborderw=12"
            : "";
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "gradients=s=1280x720:r=30:d=8:speed=0.02:nb_colors=4:c0=0x1d2b53:c1=0x7e2553:c2=0x008751:c3=0xab5236",
            "-f", "lavfi", "-i", "mandelbrot=s=640x360:r=30,scale=1280:720,trim=duration=8",
            "-f", "lavfi", "-i", "life=s=320x180:r=30:mold=8:ratio=0.12:life_color=0xE5484D:death_color=0x15151B,scale=1280:720:flags=neighbor,trim=duration=8",
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=8",
            "-f", "lavfi", "-i", "aevalsrc='0.22*sin(2*PI*(196+49*floor(mod(t*2,4)))*t)*(0.55+0.45*sin(2*PI*1.5*t))|0.22*sin(2*PI*(196+49*floor(mod(t*2,4)))*t)*(0.55+0.45*sin(2*PI*1.5*t))':s=48000:d=32",
            "-f", "lavfi", "-i", "anoisesrc=d=32:c=pink:a=0.45:r=48000",
            "-filter_complex",
            $"[0:v][1:v][2:v][3:v]concat=n=4:v=1:a=0,format=yuv420p{clock}[v];[5:a]volume='if(lt(mod(t,3.2),1.4),1,0.03)':eval=frame,lowpass=f=3000[mic]",
            "-map", "[v]", "-map", "4:a", "-map", "[mic]",
            "-c:v", "libx264", "-preset", "veryfast", "-b:v", "6M", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "160k",
            "-metadata:s:a:0", "title=game", "-metadata:s:a:1", "title=mic",
            "-metadata:s:a:0", "handler_name=game", "-metadata:s:a:1", "handler_name=mic",
            "-t", "32", video,
        };
        var (code, err) = await ExportJob.RunFfmpegAsync(args, CancellationToken.None, log: false);
        if (code != 0) throw new InvalidOperationException("demo video: " + err);

        (code, err) = await ExportJob.RunFfmpegAsync(new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "aevalsrc='0.18*sin(2*PI*330*t)*(0.5+0.5*sin(2*PI*2*t))+0.12*sin(2*PI*495*t)':s=44100:d=10",
            "-c:a", "aac", music,
        }, CancellationToken.None, log: false);
        if (code != 0) throw new InvalidOperationException("demo music: " + err);

        // sticker drawn with wpf: rounded badge with transparent corners
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), null, new Rect(0, 0, 420, 150), 34, 34);
            var ft = new FormattedText("gg ez", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 84, Brushes.White, 1);
            dc.DrawText(ft, new Point((420 - ft.Width) / 2, (150 - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(420, 150, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using (var fs = File.Create(sticker)) enc.Save(fs);
        return (video, sticker, music);
    }

    private void StartRecording()
    {
        var b = Native.VisibleBounds(_w);
        var w = (b.Right - b.Left) & ~1;
        var h = (b.Bottom - b.Top) & ~1;
        Log($"recording {w}x{h} at {b.Left},{b.Top}");
        var psi = new ProcessStartInfo
        {
            FileName = ToolPaths.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y", "-f", "gdigrab", "-framerate", "30", "-draw_mouse", "1",
                     "-offset_x", b.Left.ToString(CultureInfo.InvariantCulture), "-offset_y", b.Top.ToString(CultureInfo.InvariantCulture),
                     "-video_size", $"{w}x{h}", "-i", "desktop",
                     "-c:v", "libx264", "-preset", "ultrafast", "-crf", "14", "-pix_fmt", "yuv444p", Path.Combine(_out, "raw.mkv"),
                 })
            psi.ArgumentList.Add(a);
        _rec = Process.Start(psi);
        _ = _rec!.StandardError.ReadToEndAsync().ContinueWith(t => { if (t.Result.Length > 0) Log("recorder: " + t.Result.Trim()); });
        _clock.Restart();
    }

    private async Task StopRecording()
    {
        if (_rec == null) return;
        try
        {
            await _rec.StandardInput.WriteAsync("q");
            await _rec.StandardInput.FlushAsync();
            await _rec.WaitForExitAsync(new CancellationTokenSource(15000).Token);
        }
        catch
        {
            Proc.Kill(_rec);
        }
        _rec = null;
    }

    private async Task MakeMedia()
    {
        var raw = Path.Combine(_out, "raw.mkv");
        if (!File.Exists(raw)) { Check(false, "recording", "no raw.mkv"); return; }
        var gifs = Path.Combine(_out, "guide");
        Directory.CreateDirectory(gifs);
        string T(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var (name, start, end) in _clips)
        {
            var (code, err) = await ExportJob.RunFfmpegAsync(new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", T(start), "-t", T(Math.Min(14, end - start)), "-i", raw,
                "-vf", "fps=15,scale=1100:-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=200:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
                "-loop", "0", Path.Combine(gifs, name + ".gif"),
            }, CancellationToken.None, log: false);
            Check(code == 0, "gif " + name, code == 0 ? $"{T(end - start)}s" : err);
        }
        foreach (var (name, at) in _stills)
        {
            await ExportJob.RunFfmpegAsync(new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", T(at), "-i", raw, "-frames:v", "1",
                Path.Combine(gifs, name + ".png"),
            }, CancellationToken.None, log: false);
        }
        // a small full-length mp4 of the whole walkthrough
        await ExportJob.RunFfmpegAsync(new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", raw, "-vf", "scale=1280:-2", "-c:v", "libx264",
            "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p", Path.Combine(_out, "walkthrough.mp4"),
        }, CancellationToken.None, log: false);
    }
}
