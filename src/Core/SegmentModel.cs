namespace Cut.Core;

public readonly record struct Segment(double Start, double End)
{
    public double Duration => End - Start;
}

// the kept regions of the timeline, sorted and non-overlapping.
// gaps between segments are the cuts. segments are addressed by index
// (records compare by value, so references are not stable identities).
public sealed class SegmentModel
{
    public const double MinLength = 0.05;

    private List<Segment> _segments = new();
    private readonly Stack<Segment[]> _undo = new();
    private readonly Stack<Segment[]> _redo = new();

    public double TotalDuration { get; private set; }
    public event Action? Changed;

    public IReadOnlyList<Segment> Segments => _segments;
    public double KeptDuration => _segments.Sum(s => s.Duration);
    public bool IsTrimmed => _segments.Count != 1
        || _segments[0].Start > 0.001
        || _segments[0].End < TotalDuration - 0.001;

    public void Reset(double duration)
    {
        TotalDuration = duration;
        _segments = new List<Segment> { new(0, duration) };
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    public int IndexAt(double t) => _segments.FindIndex(s => t >= s.Start && t <= s.End);

    public bool SplitAt(double t)
    {
        var i = _segments.FindIndex(s => t > s.Start + MinLength && t < s.End - MinLength);
        if (i < 0) return false;
        Snapshot();
        var seg = _segments[i];
        _segments[i] = new Segment(seg.Start, t);
        _segments.Insert(i + 1, new Segment(t, seg.End));
        Changed?.Invoke();
        return true;
    }

    public bool Delete(int index)
    {
        if (index < 0 || index >= _segments.Count || _segments.Count <= 1) return false;
        Snapshot();
        _segments.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    // called once when an edge drag starts, so the whole drag is one undo step
    public void BeginEdit() => Snapshot();

    public void MoveEdge(int index, bool leftEdge, double t)
    {
        if (index < 0 || index >= _segments.Count) return;
        var seg = _segments[index];
        if (leftEdge)
        {
            var min = index > 0 ? _segments[index - 1].End : 0;
            t = Math.Clamp(t, min, seg.End - MinLength);
            _segments[index] = new Segment(t, seg.End);
        }
        else
        {
            var max = index < _segments.Count - 1 ? _segments[index + 1].Start : TotalDuration;
            t = Math.Clamp(t, seg.Start + MinLength, max);
            _segments[index] = new Segment(seg.Start, t);
        }
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_segments.ToArray());
        _segments = _undo.Pop().ToList();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_segments.ToArray());
        _segments = _redo.Pop().ToList();
        Changed?.Invoke();
        return true;
    }

    private void Snapshot()
    {
        _undo.Push(_segments.ToArray());
        _redo.Clear();
    }
}
