namespace Cut.Core;

// what an exact (re-encoded) export will look like: resolution, fps, bitrates and the
// size that comes out of them. bitrate-targeted encoding is what makes the estimate honest.
public sealed record QualityPlan(
    int Width, int Height, double Fps, long VideoBitrate, int AudioBitrate,
    int AudioStreams, double Duration)
{
    public double EstimatedBytes => (VideoBitrate + (double)AudioBitrate * AudioStreams) * Duration / 8 * 1.012;
}

public static class Quality
{
    // short-side heights offered (and tried by auto), largest first
    public static readonly int[] Heights = { 2160, 1440, 1080, 720, 540, 480, 360, 240 };

    // below this many bits per pixel per frame h.264 starts to fall apart
    private const double MinBpp = 0.045;

    public const double DiscordBytes = 9_500_000; // 10 mb limit with headroom

    // "original" size: the source's own bitrate over the exported duration
    public static double OriginalBytes(MediaInfo m, double duration, int audioStreams)
        => (m.EstimatedVideoBitRate + 160_000.0 * audioStreams) * duration / 8;

    public static int AudioBitrateFor(double totalBps) =>
        totalBps < 900_000 ? 96_000 : totalBps < 3_000_000 ? 128_000 : totalBps < 10_000_000 ? 160_000 : 192_000;

    // targetBytes drives everything. height/fps: 0 = auto, -1 = keep source, else explicit.
    public static QualityPlan Plan(MediaInfo m, double duration, double targetBytes, int audioStreams,
        int height, double fps)
    {
        duration = Math.Max(duration, 0.1);
        var totalBps = targetBytes * 8 / duration / 1.012;
        var abr = audioStreams > 0 ? AudioBitrateFor(totalBps) : 0;
        var vbr = (long)Math.Max(100_000, totalBps - (double)abr * audioStreams);

        var srcFps = m.Fps > 0 ? m.Fps : 30;
        var outFps = fps > 0 ? Math.Min(fps, srcFps) : srcFps;
        var srcShort = Math.Max(2, Math.Min(m.Width, m.Height));

        int shortSide;
        if (height == -1) shortSide = srcShort;
        else if (height > 0) shortSide = Math.Min(height, srcShort);
        else
        {
            // auto: biggest size that keeps enough bits per pixel; drop to 30 fps only as a last resort
            var options = Heights.Where(h => h < srcShort).Prepend(srcShort).Where(h => h >= 360 || h == srcShort).ToList();
            var fpsOptions = fps == 0 && srcFps > 31 ? new[] { srcFps, 30.0 } : new[] { outFps };
            shortSide = options[^1];
            outFps = fpsOptions[^1];
            foreach (var f in fpsOptions)
            {
                var hit = options.FirstOrDefault(h => Bpp(vbr, h, m.Aspect, f) >= MinBpp);
                if (hit == 0) continue;
                shortSide = hit;
                outFps = f;
                break;
            }
        }

        var (w, h2) = Dimensions(m, shortSide);
        return new QualityPlan(w, h2, outFps, vbr, abr, audioStreams, duration);
    }

    public static (int W, int H) Dimensions(MediaInfo m, int shortSide)
    {
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        if (m.Width <= 0 || m.Height <= 0) return (Even(shortSide * 16.0 / 9), Even(shortSide));
        if (m.Width >= m.Height)
            return (Even(shortSide * (double)m.Width / m.Height), Even(shortSide));
        return (Even(shortSide), Even(shortSide * (double)m.Height / m.Width));
    }

    private static double Bpp(long vbr, int shortSide, double aspect, double fps)
    {
        var longSide = shortSide * Math.Max(aspect, 1 / aspect);
        return vbr / (shortSide * longSide * fps);
    }

    // the size slider is logarithmic: 0 → 1% of original, 1 → 100%
    public static double SliderToFraction(double s) => Math.Pow(10, -2 + 2 * Math.Clamp(s, 0, 1));
    public static double FractionToSlider(double f) => Math.Clamp((Math.Log10(Math.Max(f, 0.01)) + 2) / 2, 0, 1);
}
