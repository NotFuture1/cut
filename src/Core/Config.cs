using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cut.Core;

public sealed class Config
{
    [JsonPropertyName("windowWidth")] public double WindowWidth { get; set; } = 1100;
    [JsonPropertyName("windowHeight")] public double WindowHeight { get; set; } = 700;
    [JsonPropertyName("lastDir")] public string? LastDir { get; set; }
    [JsonPropertyName("previewVolume")] public double PreviewVolume { get; set; } = 100;

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
