using System.Collections.Immutable;

namespace Cut.Core;

// pure edit operations: state in, new state out (or the same instance when nothing changes)
public static class Edits
{
    public const double MinLength = 0.04;

    // ---- kept segments ----

    public static int SegmentAt(ProjectState s, double t)
    {
        for (int i = 0; i < s.Segments.Length; i++)
            if (t >= s.Segments[i].Start && t <= s.Segments[i].End) return i;
        return -1;
    }

    public static ProjectState Split(ProjectState s, double t)
    {
        for (int i = 0; i < s.Segments.Length; i++)
        {
            var seg = s.Segments[i];
            if (t > seg.Start + MinLength && t < seg.End - MinLength)
            {
                var b = s.Segments.ToBuilder();
                b[i] = seg with { End = t };
                b.Insert(i + 1, new Segment(t, seg.End));
                return s with { Segments = b.ToImmutable() };
            }
        }
        return s;
    }

    public static ProjectState DeleteSegment(ProjectState s, int index)
    {
        if (index < 0 || index >= s.Segments.Length || s.Segments.Length <= 1) return s;
        return s with { Segments = s.Segments.RemoveAt(index) };
    }

    // removes [a,b] from the kept regions, whatever segments it touches
    public static ProjectState CutRange(ProjectState s, double a, double b)
    {
        if (b - a < MinLength / 2) return s;
        var result = ImmutableArray.CreateBuilder<Segment>();
        foreach (var seg in s.Segments)
        {
            if (seg.End <= a || seg.Start >= b) { result.Add(seg); continue; }
            if (seg.Start < a && a - seg.Start >= MinLength) result.Add(new Segment(seg.Start, a));
            if (seg.End > b && seg.End - b >= MinLength) result.Add(new Segment(b, seg.End));
        }
        if (result.Count == 0) return s; // never cut everything
        return s with { Segments = result.ToImmutable() };
    }

    // drags one edge, clamped between its neighbours
    public static ProjectState MoveSegmentEdge(ProjectState s, int index, bool left, double t, double total)
    {
        if (index < 0 || index >= s.Segments.Length) return s;
        var seg = s.Segments[index];
        Segment next;
        if (left)
        {
            var min = index > 0 ? s.Segments[index - 1].End : 0;
            next = seg with { Start = Math.Clamp(t, min, seg.End - MinLength) };
        }
        else
        {
            var max = index < s.Segments.Length - 1 ? s.Segments[index + 1].Start : total;
            next = seg with { End = Math.Clamp(t, seg.Start + MinLength, max) };
        }
        return s with { Segments = s.Segments.SetItem(index, next) };
    }

    public static ProjectState SetSegment(ProjectState s, int index, double start, double end, double total)
    {
        if (index < 0 || index >= s.Segments.Length) return s;
        var min = index > 0 ? s.Segments[index - 1].End : 0;
        var max = index < s.Segments.Length - 1 ? s.Segments[index + 1].Start : total;
        start = Math.Clamp(start, min, max - MinLength);
        end = Math.Clamp(end, start + MinLength, max);
        return s with { Segments = s.Segments.SetItem(index, new Segment(start, end)) };
    }

    // ---- audio lanes ----

    public static ProjectState UpdateLane(ProjectState s, int laneId, Func<Lane, Lane> f)
    {
        for (int i = 0; i < s.AudioLanes.Length; i++)
            if (s.AudioLanes[i].Id == laneId)
                return s with { AudioLanes = s.AudioLanes.SetItem(i, f(s.AudioLanes[i])) };
        for (int i = 0; i < s.ClipLanes.Length; i++)
            if (s.ClipLanes[i].Id == laneId)
                return s with { ClipLanes = s.ClipLanes.SetItem(i, f(s.ClipLanes[i])) };
        return s;
    }

    // adds a mute range, merging with any it touches
    public static ProjectState Mute(ProjectState s, int laneId, double a, double b)
    {
        if (b - a < 0.01) return s;
        return UpdateLane(s, laneId, l => l with { Mutes = Merge(l.Mutes.Add(new Segment(a, b))) });
    }

    // removes [a,b] from a lane's mutes (unmute part)
    public static ProjectState Unmute(ProjectState s, int laneId, double a, double b)
        => UpdateLane(s, laneId, l => l with { Mutes = Subtract(l.Mutes, a, b) });

    public static ProjectState RemoveMute(ProjectState s, int laneId, int index)
        => UpdateLane(s, laneId, l => index >= 0 && index < l.Mutes.Length
            ? l with { Mutes = l.Mutes.RemoveAt(index) } : l);

    public static ProjectState MoveMuteEdge(ProjectState s, int laneId, int index, bool left, double t, double total)
        => UpdateLane(s, laneId, l =>
        {
            if (index < 0 || index >= l.Mutes.Length) return l;
            var m = l.Mutes[index];
            var min = index > 0 ? l.Mutes[index - 1].End : 0;
            var max = index < l.Mutes.Length - 1 ? l.Mutes[index + 1].Start : total;
            var next = left
                ? m with { Start = Math.Clamp(t, min, m.End - 0.01) }
                : m with { End = Math.Clamp(t, m.Start + 0.01, max) };
            return l with { Mutes = l.Mutes.SetItem(index, next) };
        });

    public static ProjectState SetMute(ProjectState s, int laneId, int index, double a, double b)
        => UpdateLane(s, laneId, l =>
        {
            if (index < 0 || index >= l.Mutes.Length || b - a < 0.01) return l;
            return l with { Mutes = Merge(l.Mutes.SetItem(index, new Segment(a, b))) };
        });

    public static ImmutableArray<Segment> Merge(ImmutableArray<Segment> spans)
    {
        var sorted = spans.Where(x => x.Duration > 0).OrderBy(x => x.Start).ToList();
        var outp = ImmutableArray.CreateBuilder<Segment>();
        foreach (var x in sorted)
        {
            if (outp.Count > 0 && x.Start <= outp[^1].End + 1e-6)
                outp[^1] = outp[^1] with { End = Math.Max(outp[^1].End, x.End) };
            else
                outp.Add(x);
        }
        return outp.ToImmutable();
    }

    public static ImmutableArray<Segment> Subtract(ImmutableArray<Segment> spans, double a, double b)
    {
        var outp = ImmutableArray.CreateBuilder<Segment>();
        foreach (var x in spans)
        {
            if (x.End <= a || x.Start >= b) { outp.Add(x); continue; }
            if (x.Start < a) outp.Add(new Segment(x.Start, a));
            if (x.End > b) outp.Add(new Segment(b, x.End));
        }
        return outp.ToImmutable();
    }

    // reorders a lane within its group (clip lanes or audio lanes)
    public static ProjectState MoveLane(ProjectState s, int laneId, int newIndex)
    {
        static ImmutableArray<Lane> Move(ImmutableArray<Lane> lanes, int id, int to)
        {
            var from = lanes.IndexOf(lanes.First(l => l.Id == id));
            to = Math.Clamp(to, 0, lanes.Length - 1);
            if (from == to) return lanes;
            var l = lanes[from];
            return lanes.RemoveAt(from).Insert(to, l);
        }
        if (s.AudioLanes.Any(l => l.Id == laneId))
            return s with { AudioLanes = Move(s.AudioLanes, laneId, newIndex) };
        if (s.ClipLanes.Any(l => l.Id == laneId))
            return s with { ClipLanes = Move(s.ClipLanes, laneId, newIndex) };
        return s;
    }

    // ---- clips ----

    public static ProjectState AddClip(ProjectState s, Clip clip, int? laneId, out int clipId)
    {
        clipId = s.NextId;
        clip = clip with { Id = s.NextId };
        var nextId = s.NextId + 1;

        // drop onto the requested lane if it is free there, else the first free lane, else a new top lane
        var lanes = s.ClipLanes;
        int target = -1;
        if (laneId is int want)
        {
            var idx = lanes.IndexOf(lanes.FirstOrDefault(l => l.Id == want) ?? new Lane { Id = -999 });
            if (idx >= 0 && Fits(lanes[idx], clip, -1)) target = idx;
        }
        if (target < 0)
            for (int i = lanes.Length - 1; i >= 0; i--)
                if (Fits(lanes[i], clip, -1)) { target = i; break; }

        if (target < 0)
        {
            var lane = new Lane
            {
                Id = nextId++,
                Kind = LaneKind.Clips,
                Name = clip.Kind == ClipKind.Audio ? "audio" : "layer",
                Clips = ImmutableArray.Create(clip),
            };
            lanes = lanes.Insert(0, lane);
        }
        else
        {
            lanes = lanes.SetItem(target, lanes[target] with { Clips = Sorted(lanes[target].Clips.Add(clip)) });
        }
        return s with { ClipLanes = NameLanes(lanes), NextId = nextId };
    }

    public static ProjectState UpdateClip(ProjectState s, int clipId, Func<Clip, Clip> f)
    {
        for (int i = 0; i < s.ClipLanes.Length; i++)
        {
            var lane = s.ClipLanes[i];
            for (int j = 0; j < lane.Clips.Length; j++)
            {
                if (lane.Clips[j].Id != clipId) continue;
                var c = Normalize(f(lane.Clips[j]));
                return s with { ClipLanes = s.ClipLanes.SetItem(i, lane with { Clips = Sorted(lane.Clips.SetItem(j, c)) }) };
            }
        }
        return s;
    }

    public static ProjectState RemoveClip(ProjectState s, int clipId)
    {
        var lanes = s.ClipLanes.Select(l => l with { Clips = l.Clips.RemoveAll(c => c.Id == clipId) })
            .Where(l => l.Clips.Length > 0).ToImmutableArray();
        return s with { ClipLanes = NameLanes(lanes) };
    }

    // moves a clip in time and optionally to another clip lane (laneIndex -1 = new top lane)
    public static ProjectState MoveClip(ProjectState s, int clipId, double start, int targetLaneIndex, double total)
    {
        var found = s.FindClip(clipId);
        if (found == null) return s;
        var (fromLane, clip) = found.Value;
        start = Math.Clamp(start, 0, Math.Max(0, total - clip.Duration));
        var moved = clip with { Start = start };

        var lanes = s.ClipLanes;
        var fromIdx = lanes.IndexOf(fromLane);
        if (targetLaneIndex == fromIdx || targetLaneIndex >= lanes.Length)
        {
            if (!Fits(fromLane, moved, clipId)) moved = moved with { Start = NearestFree(fromLane, moved, clipId, total) };
            var l = fromLane with { Clips = Sorted(fromLane.Clips.Replace(clip, moved)) };
            return s with { ClipLanes = lanes.SetItem(fromIdx, l) };
        }

        lanes = lanes.SetItem(fromIdx, fromLane with { Clips = fromLane.Clips.Remove(clip) });
        if (targetLaneIndex < 0)
        {
            lanes = lanes.Insert(0, new Lane { Id = s.NextId, Kind = LaneKind.Clips, Clips = ImmutableArray.Create(moved) });
            s = s with { NextId = s.NextId + 1 };
        }
        else
        {
            var to = lanes[targetLaneIndex];
            if (!Fits(to, moved, clipId)) moved = moved with { Start = NearestFree(to, moved, clipId, total) };
            lanes = lanes.SetItem(targetLaneIndex, to with { Clips = Sorted(to.Clips.Add(moved)) });
        }
        lanes = lanes.Where(l => l.Clips.Length > 0).ToImmutableArray();
        return s with { ClipLanes = NameLanes(lanes) };
    }

    // trims a clip from its left or right edge. left edges also move the media in-point.
    public static ProjectState TrimClip(ProjectState s, int clipId, bool left, double t, double total)
    {
        var found = s.FindClip(clipId);
        if (found == null) return s;
        var (lane, c) = found.Value;
        double prevEnd = 0, nextStart = total;
        foreach (var o in lane.Clips)
        {
            if (o.Id == c.Id) continue;
            if (o.End <= c.Start + 1e-6) prevEnd = Math.Max(prevEnd, o.End);
            if (o.Start >= c.End - 1e-6) nextStart = Math.Min(nextStart, o.Start);
        }
        var bounded = c.MediaDuration > 0 && c.Kind != ClipKind.Gif;
        if (left)
        {
            var min = Math.Max(prevEnd, bounded ? c.Start - c.In : 0);
            var start = Math.Clamp(t, min, c.End - MinLength);
            var delta = start - c.Start;
            c = c with { Start = start, Duration = c.Duration - delta, In = Math.Max(0, c.In + delta) };
        }
        else
        {
            var max = nextStart;
            if (bounded) max = Math.Min(max, c.Start + (c.MediaDuration - c.In));
            var end = Math.Clamp(t, c.Start + MinLength, max);
            c = c with { Duration = end - c.Start };
        }
        return UpdateClip(s, clipId, _ => c);
    }

    private static Clip Normalize(Clip c) => c with
    {
        X = Math.Clamp(c.X, -0.5, 1.5),
        Y = Math.Clamp(c.Y, -0.5, 1.5),
        W = Math.Clamp(c.W, 0.02, 2),
        Opacity = Math.Clamp(c.Opacity, 0, 1),
        Volume = Math.Clamp(c.Volume, 0, 2),
        Duration = Math.Max(MinLength, c.Duration),
        In = Math.Max(0, c.In),
    };

    private static ImmutableArray<Clip> Sorted(ImmutableArray<Clip> clips)
        => clips.Sort((a, b) => a.Start.CompareTo(b.Start));

    private static bool Fits(Lane lane, Clip c, int ignoreId)
        => lane.Clips.All(o => o.Id == ignoreId || o.Id == c.Id || o.End <= c.Start + 1e-6 || o.Start >= c.End - 1e-6);

    // closest start where the clip doesn't overlap neighbours on this lane
    private static double NearestFree(Lane lane, Clip c, int ignoreId, double total)
    {
        var others = lane.Clips.Where(o => o.Id != ignoreId).OrderBy(o => o.Start).ToList();
        var candidates = new List<double> { c.Start };
        foreach (var o in others)
        {
            candidates.Add(o.End);
            candidates.Add(o.Start - c.Duration);
        }
        double best = c.Start, bestDist = double.MaxValue;
        foreach (var cand in candidates)
        {
            if (cand < 0 || cand + c.Duration > total + 1e-6) continue;
            var test = c with { Start = cand };
            if (!others.All(o => o.End <= test.Start + 1e-6 || o.Start >= test.End - 1e-6)) continue;
            var d = Math.Abs(cand - c.Start);
            if (d < bestDist) { bestDist = d; best = cand; }
        }
        return best;
    }

    // lanes are named by what they hold, so the headers stay meaningful after moves
    private static ImmutableArray<Lane> NameLanes(ImmutableArray<Lane> lanes)
    {
        var b = lanes.ToBuilder();
        for (int i = 0; i < b.Count; i++)
        {
            var hasVisual = b[i].Clips.Any(c => c.IsVisual);
            var hasAudio = b[i].Clips.Any(c => !c.IsVisual);
            var name = hasVisual && !hasAudio ? "layer" : !hasVisual && hasAudio ? "audio" : "layer";
            b[i] = b[i] with { Name = name };
        }
        return b.ToImmutable();
    }
}
