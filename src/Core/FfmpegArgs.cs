using System.Globalization;
using System.Text;

namespace Cut.Core;

// pure builders, no side effects. args are List<string> handed to ProcessStartInfo.ArgumentList,
// so nothing is ever shell-quoted. all numbers formatted with InvariantCulture.
public static class FfmpegArgs
{
    private static string T(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static List<string> Base() => new()
    {
        "-hide_banner", "-loglevel", "warning", "-nostats", "-y", "-progress", "pipe:1",
    };

    private static bool IsMp4Like(string ext) => ext is "mp4" or "mov" or "m4v";

    // one kept segment, stream copy. input seek before -i snaps to the keyframe
    // at or before start, which is what stream copy needs. mkv intermediate holds any codec.
    public static List<string> LosslessSegment(string input, Segment seg,
        IReadOnlyList<TrackSetting> tracks, string segPath)
    {
        var args = Base();
        args.AddRange(new[] { "-ss", T(seg.Start), "-i", input, "-t", T(seg.Duration) });
        args.AddRange(new[] { "-map", "0:v:0" });
        foreach (var t in tracks)
            args.AddRange(new[] { "-map", $"0:a:{t.RelIndex}" });
        args.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero", segPath });
        return args;
    }

    // join the segment files. volume is applied here, not per segment, so a changed
    // track gets one clean encode across the whole result instead of aac gaps at every joint.
    public static List<string> ConcatJoin(string listFile,
        IReadOnlyList<TrackSetting> tracks, string containerExt, string outPath)
    {
        var args = Base();
        args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", listFile, "-map", "0", "-c:v", "copy" });
        for (int j = 0; j < tracks.Count; j++)
        {
            if (tracks[j].NeedsReencode)
            {
                args.AddRange(new[]
                {
                    $"-filter:a:{j}", $"volume={T(tracks[j].Volume)}",
                    $"-c:a:{j}", "aac", $"-b:a:{j}", "192k",
                });
            }
            else
            {
                args.AddRange(new[] { $"-c:a:{j}", "copy" });
            }
        }
        if (IsMp4Like(containerExt))
            args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(outPath);
        return args;
    }

    // single pass, frame accurate: trim/atrim each kept segment, concat, per-track volume after concat.
    public static List<string> Precise(string input, IReadOnlyList<Segment> segments,
        IReadOnlyList<TrackSetting> tracks, int crf, string containerExt, string outPath)
    {
        int n = segments.Count, m = tracks.Count;
        var fc = new StringBuilder();

        for (int s = 0; s < n; s++)
            fc.Append($"[0:v:0]trim=start={T(segments[s].Start)}:end={T(segments[s].End)},setpts=PTS-STARTPTS[v{s}];");
        for (int s = 0; s < n; s++)
            for (int j = 0; j < m; j++)
                fc.Append($"[0:a:{tracks[j].RelIndex}]atrim=start={T(segments[s].Start)}:end={T(segments[s].End)},asetpts=PTS-STARTPTS[a{s}_{j}];");

        for (int s = 0; s < n; s++)
        {
            fc.Append($"[v{s}]");
            for (int j = 0; j < m; j++) fc.Append($"[a{s}_{j}]");
        }
        fc.Append($"concat=n={n}:v=1:a={m}[vc]");
        for (int j = 0; j < m; j++) fc.Append($"[ac{j}]");

        var audioOutLabels = new string[m];
        for (int j = 0; j < m; j++)
        {
            if (tracks[j].NeedsReencode)
            {
                fc.Append($";[ac{j}]volume={T(tracks[j].Volume)}[ao{j}]");
                audioOutLabels[j] = $"[ao{j}]";
            }
            else
            {
                audioOutLabels[j] = $"[ac{j}]";
            }
        }

        var args = Base();
        args.AddRange(new[] { "-i", input, "-filter_complex", fc.ToString(), "-map", "[vc]" });
        foreach (var label in audioOutLabels)
            args.AddRange(new[] { "-map", label });

        args.AddRange(new[]
        {
            "-c:v", "libx264", "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-preset", "medium", "-pix_fmt", "yuv420p",
        });
        if (m > 0)
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        if (IsMp4Like(containerExt))
            args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(outPath);
        return args;
    }

    // selftest fixture: 10s test pattern with two sine audio tracks, keyframe every second
    public static List<string> Fixture(string outPath) => new()
    {
        "-hide_banner", "-loglevel", "warning", "-nostats", "-y",
        "-f", "lavfi", "-i", "testsrc2=duration=10:size=640x360:rate=30",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=10",
        "-f", "lavfi", "-i", "sine=frequency=880:duration=10",
        "-map", "0:v", "-map", "1:a", "-map", "2:a",
        "-c:v", "libx264", "-preset", "veryfast", "-g", "30",
        "-c:a", "aac", outPath,
    };

    // concat demuxer list file content; ffmpeg wants forward slashes and quoted paths
    public static string ConcatListLine(string path)
        => $"file '{path.Replace('\\', '/').Replace("'", "'\\''")}'";
}
