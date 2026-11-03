using System.Globalization;
using System.Text;

namespace Cut.Core;

public sealed class FfmpegRun
{
    public List<string> Args = new();
    public double Seconds;      // media seconds of work, for progress
    public string Status = "";
}

// pure builders, no side effects. args are List<string> handed to ProcessStartInfo.ArgumentList,
// so nothing is ever shell-quoted. all numbers formatted with InvariantCulture.
public sealed class ExportPlan
{
    public List<FfmpegRun> Runs = new();
    public double OutputDuration;
    public QualityPlan? Quality;
    public int SkippedCuts;
    public string? ListFile;
    public List<string> ListLines = new();
    public List<(string File, List<string> Lines)> ExtraLists = new();

    private static string T(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static List<string> Base() => new()
    {
        "-hide_banner", "-loglevel", "warning", "-nostats", "-nostdin", "-y", "-progress", "pipe:1",
    };

    public static bool IsMp4Like(string ext) => ext is "mp4" or "mov" or "m4v";

    private static readonly HashSet<string> Mp4Audio = new() { "aac", "mp3", "ac3", "eac3", "opus", "alac" };

    // how many audio streams the output will carry
    public static int OutputAudioStreams(ProjectState s, ExportOptions o)
    {
        var lanes = s.AudioLanes.Count(l => l.Enabled);
        var clips = s.SoundClips.Any(c => o.Mode == ExportMode.Exact || c.Kind == ClipKind.Audio);
        if (o.Audio == AudioMode.Mix) return lanes > 0 || clips ? 1 : 0;
        return lanes > 0 ? lanes : clips ? 1 : 0;
    }

    public static ExportPlan Build(MediaInfo m, ProjectState s, ExportOptions o, string staging, string tmpOut)
        => o.Mode == ExportMode.Fast ? BuildFast(m, s, o, staging, tmpOut) : BuildExact(m, s, o, tmpOut);

    // ---- shared audio graph ----

    private sealed record SoundSource(string Pad, double Volume, double OutStart);

    // lanes: (input pad of the lane after concat/join, lane). returns output pads in stream order.
    private static List<string> AudioGraph(List<string> fc, List<(string Pad, Lane Lane)> lanes,
        List<SoundSource> sounds, TimeMap map, AudioMode mode)
    {
        var laneOuts = new List<string>();
        for (int j = 0; j < lanes.Count; j++)
        {
            var (pad, lane) = lanes[j];
            var parts = new List<string>();
            if (Math.Abs(lane.Volume - 1) > 0.001) parts.Add($"volume={T(lane.Volume)}");
            var mutes = lane.Mutes.SelectMany(mu => map.Pieces(mu.Start, mu.Duration)).ToList();
            if (mutes.Count > 0)
                parts.Add($"volume=0:enable='{string.Join("+", mutes.Select(p => $"between(t,{T(p.OutStart)},{T(p.OutEnd)})"))}'");
            if (parts.Count == 0)
            {
                laneOuts.Add(pad);
                continue;
            }
            fc.Add($"{pad}{string.Join(",", parts)}[lo{j}]");
            laneOuts.Add($"[lo{j}]");
        }

        var soundOuts = new List<string>();
        for (int i = 0; i < sounds.Count; i++)
        {
            var sn = sounds[i];
            var ms = (long)Math.Round(sn.OutStart * 1000);
            fc.Add($"{sn.Pad}asetpts=PTS-STARTPTS,volume={T(sn.Volume)},adelay=delays={I(ms)}:all=1[cs{i}]");
            soundOuts.Add($"[cs{i}]");
        }

        string Mix(List<string> pads, string label)
        {
            if (pads.Count == 1) return pads[0];
            fc.Add($"{string.Concat(pads)}amix=inputs={pads.Count}:normalize=0:dropout_transition=0:duration=longest[{label}]");
            return $"[{label}]";
        }

        if (mode == AudioMode.Mix || laneOuts.Count == 0)
        {
            var all = laneOuts.Concat(soundOuts).ToList();
            return all.Count == 0 ? new List<string>() : new List<string> { Mix(all, "amix") };
        }

        var outs = new List<string>(laneOuts);
        if (soundOuts.Count > 0) outs[0] = Mix(soundOuts.Prepend(laneOuts[0]).ToList(), "amix0");
        return outs;
    }

    private static string Mute(IEnumerable<Piece> pieces)
        => string.Join("+", pieces.Select(p => $"between(t,{T(p.OutStart)},{T(p.OutEnd)})"));

    // ---- exact: one ffmpeg pass, frame accurate ----

    private static ExportPlan BuildExact(MediaInfo m, ProjectState s, ExportOptions o, string tmpOut)
    {
        var plan = new ExportPlan();
        var segs = s.Segments.ToList();
        var map = new TimeMap(segs);
        var total = map.Total;
        var lanes = s.AudioLanes.Where(l => l.Enabled).ToList();
        var audioStreams = OutputAudioStreams(s, o);
        var target = o.TargetBytes > 0 ? o.TargetBytes : Core.Quality.OriginalBytes(m, total, audioStreams);
        var q = Core.Quality.Plan(m, total, target, audioStreams, o.Height, o.Fps);
        plan.Quality = q;
        plan.OutputDuration = total;

        var inputs = new List<string>();
        int nIn = 0;
        var fc = new List<string>();

        // each kept segment is its own seeked input: only kept footage is ever decoded
        foreach (var seg in segs)
        {
            if (o.HwDecode) inputs.AddRange(new[] { "-hwaccel", "auto" });
            inputs.AddRange(new[] { "-ss", T(seg.Start), "-t", T(seg.Duration), "-i", m.FilePath });
            nIn++;
        }

        var cat = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            cat.Append($"[{i}:v:0]");
            foreach (var l in lanes) cat.Append($"[{i}:a:{l.SourceRel}]");
        }
        cat.Append($"concat=n={segs.Count}:v=1:a={lanes.Count}[vc]");
        for (int j = 0; j < lanes.Count; j++) cat.Append($"[lc{j}]");
        fc.Add(cat.ToString());

        var vchain = new List<string>();
        if (m.Fps > 0 && q.Fps < m.Fps - 0.01) vchain.Add($"fps={T(q.Fps)}");
        if (q.Width != m.Width || q.Height != m.Height) vchain.Add($"scale={q.Width}:{q.Height}:flags=bicubic");
        vchain.Add("setsar=1");
        fc.Add($"[vc]{string.Join(",", vchain)}[vb0]");
        var cur = "vb0";
        int ovN = 0;

        var sounds = new List<SoundSource>();
        var W = q.Width;
        var H = q.Height;
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

        // overlays bottom layer first (clip lanes are stored top first)
        foreach (var lane in s.ClipLanes.Reverse())
        {
            if (!lane.Enabled) continue;
            foreach (var c in lane.Clips)
            {
                var pieces = map.Pieces(c.Start, c.Duration);
                if (pieces.Count == 0) continue;

                if (c.Kind == ClipKind.Audio)
                {
                    if (!c.MakesSound) continue;
                    foreach (var p in pieces)
                    {
                        var local = c.In + (p.SourceStart - c.Start);
                        inputs.AddRange(new[] { "-ss", T(local), "-t", T(p.Duration), "-i", c.Path });
                        sounds.Add(new SoundSource($"[{nIn}:a:0]", c.Volume, p.OutStart));
                        nIn++;
                    }
                    continue;
                }

                var mediaAspect = c.MediaWidth > 0 && c.MediaHeight > 0 ? (double)c.MediaHeight / c.MediaWidth : 9.0 / 16;
                var ow = Even(c.W * W);
                var oh = Even(ow * mediaAspect);
                var x = (int)Math.Round(c.X * W - ow / 2.0);
                var y = (int)Math.Round(c.Y * H - oh / 2.0);
                var fx = $"scale={ow}:{oh}:flags=bicubic,format=rgba"
                         + (c.Opacity < 0.999 ? $",colorchannelmixer=aa={T(c.Opacity)}" : "");

                if (c.Kind == ClipKind.Image)
                {
                    inputs.AddRange(new[] { "-i", c.Path });
                    var k = nIn++;
                    var ov = $"ov{ovN++}";
                    var next = $"vb{ovN}";
                    fc.Add($"[{k}:v:0]{fx}[{ov}]");
                    fc.Add($"[{cur}][{ov}]overlay=x={x}:y={y}:eof_action=repeat:enable='{Mute(pieces)}'[{next}]");
                    cur = next;
                    continue;
                }

                foreach (var p in pieces)
                {
                    var local = c.In + (p.SourceStart - c.Start);
                    string pre = "";
                    if (c.Kind == ClipKind.Gif)
                    {
                        inputs.AddRange(new[] { "-ignore_loop", "0", "-t", T(local + p.Duration), "-i", c.Path });
                        pre = $"trim=start={T(local)},";
                    }
                    else
                    {
                        inputs.AddRange(new[] { "-ss", T(local), "-t", T(p.Duration), "-i", c.Path });
                    }
                    var k = nIn++;
                    var ov = $"ov{ovN++}";
                    var next = $"vb{ovN}";
                    fc.Add($"[{k}:v:0]{pre}setpts=PTS-STARTPTS+{T(p.OutStart)}/TB,{fx}[{ov}]");
                    fc.Add($"[{cur}][{ov}]overlay=x={x}:y={y}:eof_action=pass:enable='between(t,{T(p.OutStart)},{T(p.OutEnd)})'[{next}]");
                    cur = next;
                    if (c.Kind == ClipKind.Video && c.MakesSound)
                        sounds.Add(new SoundSource($"[{k}:a:0]", c.Volume, p.OutStart));
                }
            }
        }
        fc.Add($"[{cur}]format={Encoders.PixFmt(o.Encoder)}[vout]");

        var lanePads = lanes.Select((l, j) => ($"[lc{j}]", l)).ToList();
        var audioOuts = AudioGraph(fc, lanePads, sounds, map, o.Audio);

        var args = Base();
        args.AddRange(inputs);
        args.AddRange(new[] { "-filter_complex", string.Join(";", fc), "-map", "[vout]" });
        foreach (var a in audioOuts) args.AddRange(new[] { "-map", a });
        args.AddRange(Encoders.Args(o.Encoder, q.VideoBitrate, q.Fps));
        if (audioOuts.Count > 0)
            args.AddRange(new[] { "-c:a", "aac", "-b:a", I(q.AudioBitrate) });
        AddTitles(args, s, o, audioOuts.Count);
        args.AddRange(new[] { "-t", T(total), "-max_muxing_queue_size", "4096" });
        if (IsMp4Like(o.ContainerExt)) args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(tmpOut);

        plan.Runs.Add(new FfmpegRun { Args = args, Seconds = total, Status = "encoding" });
        return plan;
    }

    // ---- fast: stream copy each kept segment, then join ----

    private static ExportPlan BuildFast(MediaInfo m, ProjectState s, ExportOptions o, string staging, string tmpOut)
    {
        var plan = new ExportPlan();
        var start = o.Start ?? (t => new StartPoint(t, t));
        var endDts = o.EndDts ?? (_ => null);
        var (eff, skipped) = Keyframes.Effective(s.Segments, start);
        plan.SkippedCuts = skipped;
        var map = new TimeMap(eff.Select(e => e.Effective).ToList());
        plan.OutputDuration = map.Total;
        var lanes = s.AudioLanes.Where(l => l.Enabled).ToList();

        // pass 1: one ffmpeg per segment, two outputs. video stops at the decode time of the
        // first frame past the end (so no frame after it slips in); audio stops at the end itself.
        var vLines = new List<string>();
        var aLines = new List<string>();
        for (int i = 0; i < eff.Count; i++)
        {
            var seg = eff[i];
            var dur = seg.Effective.Duration;
            var vPath = Path.Combine(staging, $"v{i:00000}.mkv");
            var aPath = Path.Combine(staging, $"a{i:00000}.mkv");
            var stop = endDts(seg.End);
            var vt = stop is double d ? d - m.StartTime - seg.Ss - 0.0005 : seg.End - seg.Ss + 1;
            var a = Base();
            a.AddRange(new[] { "-ss", T(seg.Ss), "-i", m.FilePath });
            a.AddRange(new[] { "-map", "0:v:0", "-c", "copy", "-t", T(Math.Max(0.001, vt)),
                "-avoid_negative_ts", "make_zero", "-map_metadata", "-1", vPath });
            if (lanes.Count > 0)
            {
                foreach (var l in lanes) a.AddRange(new[] { "-map", $"0:a:{l.SourceRel}" });
                a.AddRange(new[] { "-c", "copy", "-t", T(seg.End - seg.Ss),
                    "-avoid_negative_ts", "make_zero", "-map_metadata", "-1", aPath });
            }
            plan.Runs.Add(new FfmpegRun { Args = a, Seconds = dur, Status = $"cutting {i + 1}/{eff.Count}" });
            // exact durations: the join never drifts, whatever the codec's frame/packet sizes
            vLines.Add(ConcatListLine(vPath));
            vLines.Add($"duration {T(dur)}");
            aLines.Add(ConcatListLine(aPath));
            aLines.Add($"duration {T(dur)}");
        }
        plan.ListFile = Path.Combine(staging, "video.txt");
        plan.ListLines = vLines;
        if (lanes.Count > 0)
        {
            plan.ExtraLists.Add((Path.Combine(staging, "audio.txt"), aLines));
        }

        var args = Base();
        args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", plan.ListFile });
        if (lanes.Count > 0) args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", plan.ExtraLists[0].File });
        int nIn = lanes.Count > 0 ? 2 : 1;
        var sounds = new List<SoundSource>();
        foreach (var lane in s.ClipLanes)
        {
            if (!lane.Enabled) continue;
            foreach (var c in lane.Clips.Where(c => c.Kind == ClipKind.Audio && c.MakesSound))
            {
                foreach (var p in map.Pieces(c.Start, c.Duration))
                {
                    var local = c.In + (p.SourceStart - c.Start);
                    args.AddRange(new[] { "-ss", T(local), "-t", T(p.Duration), "-i", c.Path });
                    sounds.Add(new SoundSource($"[{nIn}:a:0]", c.Volume, p.OutStart));
                    nIn++;
                }
            }
        }

        var mp4 = IsMp4Like(o.ContainerExt);
        bool CopyOk(Lane l) => !mp4 || Mp4Audio.Contains(m.AudioTracks.ElementAtOrDefault(l.SourceRel)?.Codec ?? "");
        var needWork = sounds.Count > 0
                       || lanes.Any(l => l.NeedsAudioWork || !CopyOk(l))
                       || (o.Audio == AudioMode.Mix && lanes.Count > 1);

        args.AddRange(new[] { "-map", "0:v:0", "-c:v", "copy" });
        int outCount;
        var pads = lanes.Select((l, j) => ($"[1:a:{j}]", l)).ToList();
        if (!needWork)
        {
            for (int j = 0; j < lanes.Count; j++) args.AddRange(new[] { "-map", $"1:a:{j}" });
            if (lanes.Count > 0) args.AddRange(new[] { "-c:a", "copy" });
            outCount = lanes.Count;
        }
        else if (o.Audio == AudioMode.Separate && lanes.Count > 0)
        {
            // untouched lanes keep their original stream; only changed ones get re-encoded
            var fc = new List<string>();
            var outs = AudioGraph(fc, pads, sounds, map, AudioMode.Separate);
            if (fc.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", fc) });
            for (int j = 0; j < outs.Count; j++)
            {
                var direct = outs[j].StartsWith("[1:a:", StringComparison.Ordinal);
                args.AddRange(new[] { "-map", direct ? outs[j].Trim('[', ']') : outs[j] });
                if (direct && CopyOk(lanes[j])) args.AddRange(new[] { $"-c:a:{j}", "copy" });
                else args.AddRange(new[] { $"-c:a:{j}", "aac", $"-b:a:{j}", "192k" });
            }
            outCount = outs.Count;
        }
        else
        {
            var fc = new List<string>();
            var outs = AudioGraph(fc, pads, sounds, map, AudioMode.Mix);
            // a lone untouched pad still needs a filter so it can be encoded
            if (fc.Count == 0 && outs.Count == 1) { fc.Add($"{outs[0]}anull[amix]"); outs[0] = "[amix]"; }
            if (fc.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", fc) });
            foreach (var a in outs) args.AddRange(new[] { "-map", a });
            if (outs.Count > 0) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            outCount = outs.Count;
        }
        AddTitles(args, s, o, outCount);
        args.AddRange(new[] { "-t", T(map.Total) });
        if (mp4) args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(tmpOut);
        plan.Runs.Add(new FfmpegRun { Args = args, Seconds = map.Total, Status = "joining" });
        return plan;
    }

    private static void AddTitles(List<string> args, ProjectState s, ExportOptions o, int outCount)
    {
        if (o.Audio != AudioMode.Separate) return;
        var lanes = s.AudioLanes.Where(l => l.Enabled).ToList();
        for (int j = 0; j < outCount && j < lanes.Count; j++)
            args.AddRange(new[] { $"-metadata:s:a:{j}", $"title={lanes[j].Name}" });
    }

    // concat demuxer list file content; ffmpeg wants forward slashes and quoted paths
    public static string ConcatListLine(string path)
        => $"file '{path.Replace('\\', '/').Replace("'", "'\\''")}'";

    // selftest fixture: test pattern with two sine audio tracks, keyframe every second
    public static List<string> Fixture(string outPath, double seconds = 10, string size = "640x360")
    {
        var d = T(seconds);
        return new()
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=duration={d}:size={size}:rate=30",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={d}",
            "-f", "lavfi", "-i", $"sine=frequency=880:duration={d}",
            "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "veryfast", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
            "-c:a", "aac", "-metadata:s:a:0", "title=game", "-metadata:s:a:1", "title=mic",
            "-metadata:s:a:0", "handler_name=game", "-metadata:s:a:1", "handler_name=mic", outPath,
        };
    }
}
