using System.Globalization;

namespace Cut.Core;

// the preview's audio: every included source track mixed live inside mpv, with lane
// volumes and muted parts applied, so what you hear is what the export writes.
// filter `t` is the source timestamp, so mutes use timeline times directly.
public static class PreviewGraph
{
    private static string T(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool Audible(Lane lane, IReadOnlySet<int> solo) =>
        lane.Enabled && (solo.Count == 0 || solo.Contains(lane.Id));

    public static string Audio(ProjectState s, IReadOnlySet<int> solo)
    {
        var lanes = s.AudioLanes.Where(l => Audible(l, solo)).ToList();
        if (lanes.Count == 0) return "";
        var parts = new List<string>();
        for (int j = 0; j < lanes.Count; j++)
        {
            var l = lanes[j];
            var chain = new List<string>();
            if (Math.Abs(l.Volume - 1) > 0.001) chain.Add($"volume={T(l.Volume)}");
            if (!l.Mutes.IsDefaultOrEmpty)
                chain.Add($"volume=0:enable='{string.Join("+", l.Mutes.Select(m => $"between(t,{T(m.Start)},{T(m.End)})"))}'");
            if (chain.Count == 0) chain.Add("anull");
            var outPad = lanes.Count == 1 ? "[ao]" : $"[p{j}]";
            parts.Add($"[aid{l.SourceRel + 1}]{string.Join(",", chain)}{outPad}");
        }
        if (lanes.Count > 1)
            parts.Add($"{string.Concat(lanes.Select((_, j) => $"[p{j}]"))}amix=inputs={lanes.Count}:normalize=0:dropout_transition=0[ao]");
        return string.Join(";", parts);
    }
}
