namespace Cut.Core;

// one slice of something (a clip, a mute) that survives the cuts, in output time
public readonly record struct Piece(double OutStart, double Duration, double SourceStart)
{
    public double OutEnd => OutStart + Duration;
}

// maps between source time (what the timeline shows) and output time (the exported file)
// through the kept segments. cuts remove a vertical slice through every lane.
public sealed class TimeMap
{
    private readonly Segment[] _segs;
    private readonly double[] _outStarts;

    public TimeMap(IReadOnlyList<Segment> segments)
    {
        _segs = segments.OrderBy(s => s.Start).ToArray();
        _outStarts = new double[_segs.Length];
        double acc = 0;
        for (int i = 0; i < _segs.Length; i++)
        {
            _outStarts[i] = acc;
            acc += _segs[i].Duration;
        }
        Total = acc;
    }

    public double Total { get; }
    public IReadOnlyList<Segment> Segments => _segs;

    // gaps collapse onto the start of the next kept segment
    public double ToOutput(double t)
    {
        for (int i = 0; i < _segs.Length; i++)
        {
            if (t < _segs[i].Start) return _outStarts[i];
            if (t <= _segs[i].End) return _outStarts[i] + t - _segs[i].Start;
        }
        return Total;
    }

    public double ToSource(double o)
    {
        for (int i = 0; i < _segs.Length; i++)
            if (o <= _outStarts[i] + _segs[i].Duration)
                return _segs[i].Start + Math.Max(0, o - _outStarts[i]);
        return _segs.Length > 0 ? _segs[^1].End : 0;
    }

    // the parts of [start, start+duration) that are kept, each placed in output time
    public List<Piece> Pieces(double start, double duration)
    {
        var end = start + duration;
        var list = new List<Piece>();
        for (int i = 0; i < _segs.Length; i++)
        {
            var a = Math.Max(start, _segs[i].Start);
            var b = Math.Min(end, _segs[i].End);
            if (b - a <= 1e-4) continue;
            list.Add(new Piece(_outStarts[i] + a - _segs[i].Start, b - a, a));
        }
        return list;
    }

    // next kept time at or after t (for skipping cuts during preview), or null past the end
    public double? NextKept(double t)
    {
        foreach (var s in _segs)
        {
            if (t < s.Start) return s.Start;
            if (t < s.End) return t;
        }
        return null;
    }
}
