using System.Diagnostics;

namespace Cut.Core;

// audio peaks for drawing, 100 buckets per second, decoded in the background by
// ffmpeg (8 kHz mono pcm on stdout). values are 0..255 on a -48..0 dB scale so speech
// and quiet parts stay visible. fills progressively; Updated fires a few times a second.
public sealed class Waveform
{
    public const int BucketsPerSec = 100;
    private const int SampleRate = 8000;
    private const int SamplesPerBucket = SampleRate / BucketsPerSec;

    private byte[] _peaks;
    private volatile int _count;

    public string File { get; }
    public int AudioRel { get; }
    public bool Done { get; private set; }
    public int Count => _count;
    public event Action? Updated;

    private Waveform(string file, int audioRel, double duration)
    {
        File = file;
        AudioRel = audioRel;
        _peaks = new byte[(int)Math.Ceiling(Math.Max(duration, 1) * BucketsPerSec) + BucketsPerSec];
    }

    public static Waveform Start(string file, int audioRel, double duration, CancellationToken ct)
    {
        var w = new Waveform(file, audioRel, duration);
        _ = Task.Run(() => w.Run(ct), ct);
        return w;
    }

    // max peak over [t0, t1), 0..255
    public byte Peak(double t0, double t1)
    {
        var a = Math.Max(0, (int)(t0 * BucketsPerSec));
        var b = Math.Min(_count, (int)Math.Ceiling(t1 * BucketsPerSec));
        if (b <= a) return a < _count ? _peaks[a] : (byte)0;
        byte m = 0;
        var p = _peaks;
        for (int i = a; i < b; i++) if (p[i] > m) m = p[i];
        return m;
    }

    public bool Covers(double t) => t * BucketsPerSec < _count || Done;

    private static byte ToByte(int maxAbs)
    {
        if (maxAbs < 130) return 0; // below -48 dB
        var db = 20 * Math.Log10(maxAbs / 32768.0);
        return (byte)Math.Clamp((db + 48) / 48 * 255, 0, 255);
    }

    private async Task Run(CancellationToken ct)
    {
        Process? p = null;
        try
        {
            p = Process.Start(Proc.Info(ToolPaths.Ffmpeg, new[]
            {
                "-v", "error", "-nostdin", "-i", File, "-vn", "-sn", "-dn",
                "-map", $"0:a:{AudioRel}", "-ac", "1", "-ar", SampleRate.ToString(),
                "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1",
            }));
            if (p == null) return;
            using var reg = ct.Register(() => Proc.Kill(p));
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);

            var stream = p.StandardOutput.BaseStream;
            var buf = new byte[64 * 1024];
            int carry = -1;         // odd byte left from the previous read
            int inBucket = 0, max = 0;
            var lastNotify = Stopwatch.StartNew();

            while (true)
            {
                var n = await stream.ReadAsync(buf, CancellationToken.None);
                if (n <= 0) break;
                int i = 0;
                if (carry >= 0)
                {
                    Feed((short)(carry | (buf[0] << 8)));
                    carry = -1;
                    i = 1;
                }
                for (; i + 1 < n; i += 2) Feed((short)(buf[i] | (buf[i + 1] << 8)));
                if (i < n) carry = buf[i];

                if (lastNotify.ElapsedMilliseconds > 200)
                {
                    lastNotify.Restart();
                    Updated?.Invoke();
                }
            }
            if (inBucket > 0) Push(max);
            await p.WaitForExitAsync(CancellationToken.None);

            void Feed(short s)
            {
                var a = s < 0 ? -s : s;
                if (a > max) max = a;
                if (++inBucket == SamplesPerBucket)
                {
                    Push(max);
                    inBucket = 0;
                    max = 0;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn($"waveform {File} a:{AudioRel}: {ex.Message}");
        }
        finally
        {
            p?.Dispose();
            Done = true;
            Updated?.Invoke();
        }
    }

    private void Push(int maxAbs)
    {
        if (_count >= _peaks.Length) Array.Resize(ref _peaks, _peaks.Length * 2);
        _peaks[_count] = ToByte(maxAbs);
        _count++;
    }
}
