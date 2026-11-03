using System.Text.Json.Serialization;

namespace Cut.Core;

// raw ffprobe json shapes - everything nullable, real files omit fields constantly

public sealed class ProbeResult
{
    [JsonPropertyName("format")] public ProbeFormat? Format { get; set; }
    [JsonPropertyName("streams")] public List<ProbeStream>? Streams { get; set; }
}

public sealed class ProbeFormat
{
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonPropertyName("start_time")] public string? StartTime { get; set; }
    [JsonPropertyName("format_name")] public string? FormatName { get; set; }
    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
    [JsonPropertyName("size")] public string? Size { get; set; }
}

public sealed class ProbeStream
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
    [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("avg_frame_rate")] public string? AvgFrameRate { get; set; }
    [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; set; }
    [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }
    [JsonPropertyName("channels")] public int? Channels { get; set; }
    [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonPropertyName("nb_frames")] public string? NbFrames { get; set; }
    [JsonPropertyName("disposition")] public ProbeDisposition? Disposition { get; set; }
    [JsonPropertyName("tags")] public ProbeTags? Tags { get; set; }
}

public sealed class ProbeDisposition
{
    [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; }
}

public sealed class ProbeTags
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("handler_name")] public string? HandlerName { get; set; }

    // mp4 keeps track names in handler_name; skip the generic ones muxers write
    public string? TrackName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            var h = HandlerName?.Trim();
            if (string.IsNullOrEmpty(h)) return null;
            var lower = h.ToLowerInvariant();
            if (lower.Contains("handler") || lower.Contains("media") || lower.Contains("mainconcept")
                || lower.Contains("google") || lower.Contains("apple") || lower == "audio" || lower == "video")
                return null;
            return h;
        }
    }
}

// friendly model the app works with

public sealed class AudioTrackInfo
{
    public int RelIndex;          // 0-based index among audio streams (mpv aid = RelIndex + 1)
    public string Codec = "";
    public int Channels;
    public int SampleRate;
    public long BitRate;
    public string? Title;
    public string? Language;

    public string ShortLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title!.Trim().ToLowerInvariant();
            return $"track {RelIndex + 1}";
        }
    }

    public string Detail
    {
        get
        {
            var parts = new List<string> { Codec.ToLowerInvariant() };
            if (Channels > 0) parts.Add(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : $"{Channels}ch");
            if (!string.IsNullOrWhiteSpace(Language) && Language != "und") parts.Add(Language!.ToLowerInvariant());
            return string.Join(" · ", parts);
        }
    }
}

public sealed class MediaInfo
{
    public string FilePath = "";
    public double Duration;       // ffprobe value; mpv's duration property wins for the timeline
    public double StartTime;      // container start time; timeline 0 == this
    public long FileSize;
    public long FormatBitRate;
    public bool HasVideo;
    public bool IsStillImage;
    public string VideoCodec = "";
    public int Width;
    public int Height;
    public double Fps;
    public long VideoBitRate;     // 0 when the container doesn't say
    public List<AudioTrackInfo> AudioTracks = new();

    public string ContainerExt => Path.GetExtension(FilePath).TrimStart('.').ToLowerInvariant();
    public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 16.0 / 9;
    public double FrameDuration => Fps > 1 ? 1.0 / Fps : 1.0 / 30;

    // best guess at the video stream's bitrate, for size estimates
    public long EstimatedVideoBitRate
    {
        get
        {
            if (VideoBitRate > 0) return VideoBitRate;
            var audio = AudioTracks.Sum(a => a.BitRate > 0 ? a.BitRate : 160_000);
            var total = FormatBitRate > 0 ? FormatBitRate
                : Duration > 0 && FileSize > 0 ? (long)(FileSize * 8 / Duration) : 8_000_000;
            return Math.Max(200_000, total - audio);
        }
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasVideo) parts.Add($"{Width}×{Height}");
            if (Fps > 0) parts.Add($"{Math.Round(Fps, 2):0.##} fps");
            if (AudioTracks.Count > 0) parts.Add(AudioTracks.Count == 1 ? "1 audio track" : $"{AudioTracks.Count} audio tracks");
            if (FileSize > 0) parts.Add(SizeFmt.Bytes(FileSize));
            return string.Join(" · ", parts);
        }
    }

    public ClipKind GuessClipKind()
    {
        var ext = ContainerExt;
        if (ext == "gif") return ClipKind.Gif;
        if (IsStillImage) return ClipKind.Image;
        if (!HasVideo) return ClipKind.Audio;
        return ClipKind.Video;
    }
}

public static class SizeFmt
{
    public static string Bytes(double b)
    {
        if (b >= 1e9) return $"{b / 1e9:0.00} GB";
        if (b >= 1e8) return $"{b / 1e6:0} MB";
        if (b >= 1e6) return $"{b / 1e6:0.0} MB";
        if (b >= 1e3) return $"{b / 1e3:0} KB";
        return $"{b:0} B";
    }

    public static string Bitrate(double bps)
    {
        if (bps >= 1e6) return $"{bps / 1e6:0.0} Mbps";
        return $"{bps / 1e3:0} kbps";
    }
}
