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
    [JsonPropertyName("format_name")] public string? FormatName { get; set; }
    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
}

public sealed class ProbeStream
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
    [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("avg_frame_rate")] public string? AvgFrameRate { get; set; }
    [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }
    [JsonPropertyName("channels")] public int? Channels { get; set; }
    [JsonPropertyName("channel_layout")] public string? ChannelLayout { get; set; }
    [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
    [JsonPropertyName("tags")] public ProbeTags? Tags { get; set; }
}

public sealed class ProbeTags
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

// friendly model the app works with

public sealed class AudioTrackInfo
{
    public int RelIndex;          // 0-based index among audio streams (mpv aid = RelIndex + 1)
    public string Codec = "";
    public int Channels;
    public string? Title;
    public string? Language;

    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Title) ? Codec : Title!;
            var parts = new List<string> { name.ToLowerInvariant() };
            if (Channels > 0) parts.Add($"{Channels}ch");
            if (!string.IsNullOrWhiteSpace(Language) && Language != "und") parts.Add(Language!.ToLowerInvariant());
            return string.Join(" · ", parts);
        }
    }
}

public sealed class MediaInfo
{
    public string FilePath = "";
    public double Duration;       // ffprobe value; mpv's duration property wins for the timeline
    public bool HasVideo;
    public string VideoCodec = "";
    public int Width;
    public int Height;
    public double Fps;
    public List<AudioTrackInfo> AudioTracks = new();

    public string ContainerExt => Path.GetExtension(FilePath).TrimStart('.').ToLowerInvariant();
}
