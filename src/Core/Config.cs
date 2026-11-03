using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cut.Core;

public sealed class Config
{
    [JsonPropertyName("windowWidth")] public double WindowWidth { get; set; } = 1280;
    [JsonPropertyName("windowHeight")] public double WindowHeight { get; set; } = 800;
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }
    [JsonPropertyName("lastDir")] public string? LastDir { get; set; }
    [JsonPropertyName("lastMediaDir")] public string? LastMediaDir { get; set; }
    [JsonPropertyName("previewVolume")] public double PreviewVolume { get; set; } = 100;
    [JsonPropertyName("skipCuts")] public bool SkipCuts { get; set; } = true;
    [JsonPropertyName("timelineHeight")] public double TimelineHeight { get; set; } = 230;
    [JsonPropertyName("inspector")] public bool InspectorOpen { get; set; } = true;

    // export defaults, remembered between exports
    [JsonPropertyName("exportMode")] public string ExportMode { get; set; } = "exact";
    [JsonPropertyName("exportAudio")] public string ExportAudio { get; set; } = "mix";
    [JsonPropertyName("exportSize")] public double ExportSizeSlider { get; set; } = 1;
    [JsonPropertyName("exportHeight")] public int ExportHeight { get; set; }
    [JsonPropertyName("exportFps")] public double ExportFps { get; set; }
    [JsonPropertyName("exportFormat")] public string ExportFormat { get; set; } = "mp4";

    [JsonPropertyName("skippedVersion")] public string? SkippedVersion { get; set; }
    [JsonPropertyName("recent")] public List<string> Recent { get; set; } = new();

    public static Config Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.ConfigFile)) ?? new Config();
        }
        catch (Exception ex)
        {
            Logger.Warn($"config load: {ex.Message}");
        }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Paths.EnsureBaseDirs();
            File.WriteAllText(Paths.ConfigFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Warn($"config save: {ex.Message}");
        }
    }
}
