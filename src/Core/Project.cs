using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cut.Core;

public readonly record struct Segment(double Start, double End)
{
    public double Duration => End - Start;
    public bool Contains(double t) => t >= Start && t < End;
}

public enum ClipKind { Image, Video, Gif, Audio }

// one imported file placed on the timeline. times are source-timeline seconds,
// so a cut slices clips exactly like it slices the main video.
public sealed record Clip
{
    public int Id { get; init; }
    public ClipKind Kind { get; init; }
    public string Path { get; init; } = "";
    public double Start { get; init; }
    public double Duration { get; init; }
    public double In { get; init; }             // offset into the media file
    public double MediaDuration { get; init; }  // 0 = unbounded (images, looping gifs)
    public int MediaWidth { get; init; }
    public int MediaHeight { get; init; }
    public bool HasAudio { get; init; }

    // placement on the frame, normalized: center x/y and width as a fraction of frame width
    public double X { get; init; } = 0.5;
    public double Y { get; init; } = 0.5;
    public double W { get; init; } = 0.35;
    public double Opacity { get; init; } = 1;

    public double Volume { get; init; } = 1;    // 0..2
    public bool Muted { get; init; }

    [JsonIgnore] public double End => Start + Duration;
    [JsonIgnore] public bool IsVisual => Kind != ClipKind.Audio;
    [JsonIgnore] public bool MakesSound => HasAudio && !Muted && Volume > 0.001;
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);

    // height as a fraction of frame height, for a frame of the given aspect (w/h)
    public double HeightFrac(double frameAspect)
    {
        if (MediaWidth <= 0 || MediaHeight <= 0) return W;
        return W * frameAspect * MediaHeight / MediaWidth;
    }
}

public enum LaneKind { Clips, Audio }

public sealed record Lane
{
    public int Id { get; init; }
    public LaneKind Kind { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;  // audio: included in export, clips: shown
    public double Volume { get; init; } = 1;    // audio lanes, 0..2

    public int SourceRel { get; init; } = -1;   // audio lanes: index among the source's audio streams
    public ImmutableArray<Segment> Mutes { get; init; } = ImmutableArray<Segment>.Empty;
    public ImmutableArray<Clip> Clips { get; init; } = ImmutableArray<Clip>.Empty;

    [JsonIgnore] public bool NeedsAudioWork => Math.Abs(Volume - 1) > 0.001 || !Mutes.IsDefaultOrEmpty;
}

// everything the user edits. immutable: undo is a stack of these.
// clip lanes are ordered top layer first; audio lanes in output stream order.
public sealed record ProjectState
{
    public ImmutableArray<Segment> Segments { get; init; } = ImmutableArray<Segment>.Empty;
    public ImmutableArray<Lane> ClipLanes { get; init; } = ImmutableArray<Lane>.Empty;
    public ImmutableArray<Lane> AudioLanes { get; init; } = ImmutableArray<Lane>.Empty;
    public int NextId { get; init; } = 1;

    [JsonIgnore] public double KeptDuration => Segments.Sum(s => s.Duration);
    [JsonIgnore] public IEnumerable<Clip> AllClips => ClipLanes.SelectMany(l => l.Clips);
    [JsonIgnore] public IEnumerable<Clip> VisibleVisualClips =>
        ClipLanes.Where(l => l.Enabled).SelectMany(l => l.Clips).Where(c => c.IsVisual);
    [JsonIgnore] public bool HasVisualClips => VisibleVisualClips.Any();
    [JsonIgnore] public IEnumerable<Clip> SoundClips =>
        ClipLanes.Where(l => l.Enabled).SelectMany(l => l.Clips).Where(c => c.MakesSound);

    public Lane? FindLane(int id) =>
        ClipLanes.FirstOrDefault(l => l.Id == id) ?? AudioLanes.FirstOrDefault(l => l.Id == id);

    public (Lane Lane, Clip Clip)? FindClip(int clipId)
    {
        foreach (var l in ClipLanes)
            foreach (var c in l.Clips)
                if (c.Id == clipId) return (l, c);
        return null;
    }

    public bool IsTrimmed(double total) => Segments.Length != 1
        || Segments[0].Start > 0.001 || Segments[0].End < total - 0.001;

    public static ProjectState Fresh(MediaInfo media, double duration)
    {
        int id = 1;
        var audio = media.AudioTracks.Select(t => new Lane
        {
            Id = id++,
            Kind = LaneKind.Audio,
            Name = t.ShortLabel,
            SourceRel = t.RelIndex,
        }).ToImmutableArray();
        return new ProjectState
        {
            Segments = ImmutableArray.Create(new Segment(0, duration)),
            AudioLanes = audio,
            NextId = id,
        };
    }
}

public enum ChangeKind
{
    Segments = 1,
    Audio = 2,     // lane volumes, mutes, enable, order
    Clips = 4,     // clips added, moved, changed
    All = 7,
}

// undo/redo + change notification. a gesture (drag) calls Begin once, Preview many
// times, then End, so the whole drag is one undo step.
public sealed class ProjectStore
{
    private readonly Stack<ProjectState> _undo = new();
    private readonly Stack<ProjectState> _redo = new();
    private ProjectState? _gestureStart;

    public ProjectState State { get; private set; } = new();
    public double TotalDuration { get; private set; }
    public event Action<ChangeKind>? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool InGesture => _gestureStart != null;
    public bool CanRedo => _redo.Count > 0;

    public void Reset(ProjectState state, double total)
    {
        TotalDuration = total;
        State = state;
        _undo.Clear();
        _redo.Clear();
        _gestureStart = null;
        Changed?.Invoke(ChangeKind.All);
    }

    public void Commit(ProjectState next, ChangeKind kind)
    {
        if (ReferenceEquals(next, State)) return;
        if (_gestureStart == null)
        {
            _undo.Push(State);
            _redo.Clear();
        }
        State = next;
        Changed?.Invoke(kind);
    }

    public void BeginGesture()
    {
        _gestureStart ??= State;
    }

    public void Preview(ProjectState next, ChangeKind kind)
    {
        State = next;
        Changed?.Invoke(kind);
    }

    public void EndGesture(ChangeKind kind)
    {
        if (_gestureStart == null) return;
        if (!ReferenceEquals(_gestureStart, State))
        {
            _undo.Push(_gestureStart);
            _redo.Clear();
        }
        _gestureStart = null;
        Changed?.Invoke(kind);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(State);
        State = _undo.Pop();
        Changed?.Invoke(ChangeKind.All);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(State);
        State = _redo.Pop();
        Changed?.Invoke(ChangeKind.All);
        return true;
    }
}
