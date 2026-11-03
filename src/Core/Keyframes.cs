using System.Collections.Concurrent;
using System.Globalization;

namespace Cut.Core;

// where a stream-copy segment really starts: Landing is the first frame you'll see,
// Ss the -ss value that produces it.
public readonly record struct StartPoint(double Landing, double Ss);

// fast (stream copy) cut geometry, measured with ffmpeg itself so the timeline can show
// exactly what an export will contain. ffmpeg's cli seek differs from a plain libavformat
// seek (it backs off ~0.13s on containers that don't seek by pts when the video has
// b-frames), so asking ffprobe would be wrong for mkv. answers are cached per file.
public sealed class Keyframes
{
    private readonly MediaInfo _media;
    private readonly ConcurrentDictionary<long, StartPoint> _starts = new();
    private readonly ConcurrentDictionary<long, double?> _ends = new();
    private readonly ConcurrentDictionary<int, double[]> _chunks = new();
    private readonly SemaphoreSlim _gate = new(3);
    private const double ChunkSec = 30;
    private const double CliBackoff = 0.1305; // 3/23 s, mirrors ffmpeg's dts heuristic, plus a hair

    public Keyframes(MediaInfo media) => _media = media;

    public event Action? Updated;

    private static string T(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static long Key(double t) => (long)Math.Round(t * 1000);

    public double? CachedLanding(double t) =>
        t <= 0.0005 ? 0 : _starts.TryGetValue(Key(t), out var v) ? v.Landing : null;

    public async Task<StartPoint> StartAsync(double s, CancellationToken ct = default)
    {
        if (s <= 0.0005) return new StartPoint(0, 0);
        if (_starts.TryGetValue(Key(s), out var cached)) return cached;

        var plain = FirstVideoPts(s, ct);
        var nudged = FirstVideoPts(s + CliBackoff, ct);
        var l1 = await plain;
        var l2 = await nudged;
        StartPoint sp;
        if (l1 == null) sp = new StartPoint(s, s);
        else if (l2 is double n && n <= s + 1e-4 && n > l1.Value + 1e-4) sp = new StartPoint(n, s + CliBackoff);
        else sp = new StartPoint(Math.Min(l1.Value, s), s);
        _starts[Key(s)] = sp;
        Updated?.Invoke();
        return sp;
    }

    // decode timestamp (absolute, container clock) of the first frame shown at or after e.
    // stream copy -t cuts by decode time, so this is the exact cutoff that keeps every
    // frame before e and nothing after. null = no frame after e (end of file).
    public async Task<double?> EndDtsAsync(double e, CancellationToken ct = default)
    {
        if (_ends.TryGetValue(Key(e), out var cached)) return cached;
        var frames = (int)Math.Ceiling((_media.Fps > 0 ? _media.Fps : 60) * 12) + 60;
        var rows = await Packets(Math.Max(0, e - 0.5), frames, ct);
        double? result = null;
        var absE = e + _media.StartTime;
        foreach (var (dts, pts) in rows)
        {
            if (pts >= absE - 1e-4)
            {
                result = dts;
                break;
            }
        }
        _ends[Key(e)] = result;
        return result;
    }

    private async Task<double?> FirstVideoPts(double ss, CancellationToken ct)
    {
        var rows = await Packets(ss, 1, ct);
        return rows.Count > 0 ? Math.Max(0, rows[0].Pts - _media.StartTime) : null;
    }

    // video packets as the ffmpeg cli sees them after an -ss seek, in decode order, absolute seconds
    private async Task<List<(double Dts, double Pts)>> Packets(double ss, int frames, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var text = await Proc.RunTextAsync(ToolPaths.Ffmpeg, new[]
            {
                "-hide_banner", "-v", "error", "-nostdin", "-copyts", "-ss", T(ss), "-i", _media.FilePath,
                "-map", "0:v:0", "-c", "copy", "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
                "-f", "framecrc", "-",
            }, ct);
            double tb = 0;
            var rows = new List<(double, double)>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    var frac = line[6..].Trim().Split('/');
                    if (frac.Length == 2 && double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                        && double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                        tb = n / d;
                    continue;
                }
                if (line.Length == 0 || line[0] == '#' || tb <= 0) continue;
                var f = line.Split(',');
                if (f.Length < 3) continue;
                if (!long.TryParse(f[1].Trim(), out var dts) || !long.TryParse(f[2].Trim(), out var pts)) continue;
                if (pts < -(1L << 60)) continue; // no pts
                if (dts < -(1L << 60)) dts = pts;
                rows.Add((dts * tb, pts * tb));
            }
            return rows;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"packet probe at {ss}: {ex.Message}");
            return new List<(double, double)>();
        }
        finally
        {
            _gate.Release();
        }
    }

    // keyframe times in [a,b], fetched in 30s chunks; returns what's cached and fetches the rest
    public List<double> InRange(double a, double b)
    {
        var result = new List<double>();
        for (int c = (int)Math.Floor(Math.Max(0, a) / ChunkSec); c <= (int)Math.Floor(Math.Max(0, b) / ChunkSec); c++)
        {
            if (_chunks.TryGetValue(c, out var ks))
            {
                foreach (var k in ks) if (k >= a && k <= b) result.Add(k);
            }
            else if (_chunks.TryAdd(c, Array.Empty<double>()))
            {
                var chunk = c;
                _ = Task.Run(() => FetchChunk(chunk));
            }
        }
        return result;
    }

    private async Task FetchChunk(int c)
    {
        await _gate.WaitAsync();
        try
        {
            var a = c * ChunkSec + _media.StartTime;
            var text = await Proc.RunTextAsync(ToolPaths.Ffprobe, new[]
            {
                "-v", "error", "-select_streams", "v:0",
                "-read_intervals", $"{T(a)}%{T(a + ChunkSec)}",
                "-show_entries", "packet=pts_time,flags",
                "-of", "csv=p=0", _media.FilePath,
            }, CancellationToken.None);
            var list = new List<double>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = line.Split(',');
                if (f.Length < 2 || !f[1].StartsWith('K')) continue;
                if (double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
                    list.Add(ts - _media.StartTime);
            }
            _chunks[c] = list.ToArray();
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warn($"keyframe chunk {c}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record FastSegment(Segment Effective, double Ss, double End);

    // fast-mode segments: each start moves to where the seek lands. a segment whose landing
    // falls inside the previous one would repeat footage, so it merges into it instead.
    public static (List<FastSegment> Segments, int SkippedCuts) Effective(
        IReadOnlyList<Segment> segments, Func<double, StartPoint> start)
    {
        var list = new List<FastSegment>();
        int skipped = 0;
        foreach (var s in segments)
        {
            var sp = start(s.Start);
            var l = Math.Min(sp.Landing, s.Start);
            if (list.Count > 0 && l <= list[^1].End + 1e-3)
            {
                var prev = list[^1];
                list[^1] = prev with { Effective = prev.Effective with { End = Math.Max(prev.End, s.End) }, End = Math.Max(prev.End, s.End) };
                skipped++;
            }
            else
            {
                list.Add(new FastSegment(new Segment(l, s.End), sp.Ss, s.End));
            }
        }
        return (list, skipped);
    }
}
