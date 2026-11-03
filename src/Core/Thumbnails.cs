using System.Collections.Concurrent;
using System.Globalization;

namespace Cut.Core;

public sealed record Thumb(double Time, int Width, int Height, byte[] Bgra);

// keyframe thumbnails for the video lane. one tiny ffmpeg run per thumb (seek, decode a
// single keyframe, scale) which is far cheaper than decoding the file. newest requests
// win, so scrolling never waits behind thumbs that already left the screen.
public sealed class Thumbnails : IDisposable
{
    private readonly MediaInfo _media;
    private readonly int _height;
    private readonly int _width;
    private readonly ConcurrentDictionary<long, Thumb> _done = new();
    private readonly ConcurrentDictionary<long, byte> _pending = new();
    private readonly ConcurrentStack<double> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    public event Action? Updated;

    public Thumbnails(MediaInfo media, int height, int workers = 2)
    {
        _media = media;
        _height = height & ~1;
        _width = Math.Max(2, (int)Math.Round(_height * media.Aspect / 2) * 2);
        for (int i = 0; i < workers; i++) _ = Task.Run(Worker);
    }

    public int Width => _width;
    public int Height => _height;

    private static long Key(double t) => (long)Math.Round(t * 1000);

    public Thumb? Get(double t) => _done.TryGetValue(Key(t), out var th) ? th : null;

    public void Request(double t)
    {
        var k = Key(t);
        if (_done.ContainsKey(k) || !_pending.TryAdd(k, 0)) return;
        _queue.Push(t);
        _signal.Release();
    }

    // drop queued work that is no longer on screen
    public void Retain(Func<double, bool> keep)
    {
        var items = new List<double>();
        while (_queue.TryPop(out var t)) items.Add(t);
        items.Reverse();
        foreach (var t in items)
        {
            if (keep(t)) _queue.Push(t);
            else _pending.TryRemove(Key(t), out _);
        }
    }

    private async Task Worker()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            if (!_queue.TryPop(out var t)) continue;
            try
            {
                var abs = (t + _media.StartTime).ToString("0.###", CultureInfo.InvariantCulture);
                var px = await Proc.RunBytesAsync(ToolPaths.Ffmpeg, new[]
                {
                    "-v", "error", "-nostdin", "-skip_frame", "nokey", "-noaccurate_seek",
                    "-ss", abs, "-i", _media.FilePath, "-map", "0:v:0", "-frames:v", "1",
                    "-vf", $"scale={_width}:{_height}:flags=fast_bilinear",
                    "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
                }, ct);
                if (px.Length == _width * _height * 4)
                {
                    _done[Key(t)] = new Thumb(t, _width, _height, px);
                    Updated?.Invoke();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warn($"thumb {t}: {ex.Message}");
            }
            finally
            {
                _pending.TryRemove(Key(t), out _);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
