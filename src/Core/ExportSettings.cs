namespace Cut.Core;

public enum ExportMode
{
    Lossless, // stream copy, cuts snap to keyframes
    Precise,  // re-encode, frame accurate
}

public sealed class TrackSetting
{
    public int RelIndex;
    public bool Include = true;
    public double Volume = 1.0; // 0..2, export gain; 1.0 keeps the stream copyable in lossless mode

    public bool NeedsReencode => Include && Math.Abs(Volume - 1.0) > 0.001;
}

public sealed class ExportSettings
{
    public ExportMode Mode = ExportMode.Lossless;
    public string? ContainerExt;  // null = same as input
    public int Crf = 18;          // precise mode only
    public string OutputPath = "";
    public List<TrackSetting> Tracks = new();

    public string ResolveContainer(MediaInfo media) => ContainerExt ?? media.ContainerExt;
}
