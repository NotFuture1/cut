namespace Cut.Core;

public enum ExportMode
{
    Fast,  // stream copy: original quality, starts snap to keyframes
    Exact, // re-encode: frame accurate, overlays, size control
}

public enum AudioMode
{
    Mix,      // everything into one track (what most players and discord play)
    Separate, // one output track per audio lane
}

public sealed class ExportOptions
{
    public ExportMode Mode = ExportMode.Exact;
    public AudioMode Audio = AudioMode.Mix;
    public string ContainerExt = "mp4";
    public string OutputPath = "";

    // exact mode
    public double TargetBytes;     // <= 0 means original size
    public int Height;             // 0 auto, -1 source, else short side
    public double Fps;             // 0 auto, -1 source, else cap
    public VideoEncoder Encoder = VideoEncoder.X264;
    public bool HwDecode = true;

    // fast mode: measured cut geometry. ExportJob fills Start/EndDts from Keys before planning.
    public Keyframes? Keys;
    public Func<double, StartPoint>? Start;
    public Func<double, double?>? EndDts;

    public ExportOptions With(VideoEncoder encoder, bool hwDecode)
    {
        var o = (ExportOptions)MemberwiseClone();
        o.Encoder = encoder;
        o.HwDecode = hwDecode;
        return o;
    }
}
