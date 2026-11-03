using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cut.Core;

// edits are saved per source file (keyed by path + size + mtime) so reopening a video
// brings back its cuts, mutes and layers. small json files in %localappdata%\cut\projects.
public static class ProjectFiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string KeyFor(string mediaPath)
    {
        var fi = new FileInfo(mediaPath);
        var raw = $"{Path.GetFullPath(mediaPath).ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..20].ToLowerInvariant();
    }

    public static ProjectState? Load(string mediaPath, MediaInfo media)
    {
        try
        {
            var file = Path.Combine(Paths.ProjectsDir, KeyFor(mediaPath) + ".json");
            if (!File.Exists(file)) return null;
            var s = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(file), Json);
            if (s == null || s.Segments.IsDefaultOrEmpty) return null;
            // the file's track layout must still match
            if (s.AudioLanes.Length != media.AudioTracks.Count) return null;
            if (s.AudioLanes.Any(l => l.SourceRel < 0 || l.SourceRel >= media.AudioTracks.Count)) return null;
            // clips whose files moved are dropped rather than breaking the export
            var lanes = s.ClipLanes.Select(l => l with { Clips = l.Clips.RemoveAll(c => !File.Exists(c.Path)) })
                .Where(l => l.Clips.Length > 0).ToImmutableArray();
            return s with { ClipLanes = lanes };
        }
        catch (Exception ex)
        {
            Logger.Warn($"project load: {ex.Message}");
            return null;
        }
    }

    public static void Save(string mediaPath, ProjectState state)
    {
        try
        {
            Directory.CreateDirectory(Paths.ProjectsDir);
            var file = Path.Combine(Paths.ProjectsDir, KeyFor(mediaPath) + ".json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"project save: {ex.Message}");
        }
    }

    public static void Forget(string mediaPath)
    {
        try
        {
            var file = Path.Combine(Paths.ProjectsDir, KeyFor(mediaPath) + ".json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { /* nothing to forget */ }
    }
}
