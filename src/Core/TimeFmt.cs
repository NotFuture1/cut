using System.Globalization;

namespace Cut.Core;

public static class TimeFmt
{
    // 0:04.20, 12:34.56, 1:02:03.40
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var cs = (long)Math.Floor(seconds * 100 + 1e-6);
        var h = cs / 360000;
        var m = cs / 6000 % 60;
        var s = cs / 100 % 60;
        var c = cs % 100;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{c:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}.{c:00}");
    }

    // 0:04.200 - for edit fields and edge readouts, where the exact frame matters
    public static string Precise(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ms = (long)Math.Round(seconds * 1000);
        var h = ms / 3600000;
        var m = ms / 60000 % 60;
        var s = ms / 1000 % 60;
        var f = ms % 1000;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{f:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}.{f:000}");
    }

    // 3.2s, 1:04 - short durations for labels
    public static string Dur(double seconds)
    {
        if (seconds < 60) return seconds.ToString(seconds < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "s";
        return Clock(seconds);
    }

    // ruler labels: 0:05, 1:30, 1:02:03; sub-second steps get decimals
    public static string Ruler(double seconds, double step)
    {
        if (seconds < 0) seconds = 0;
        var whole = Math.Floor(seconds + 1e-6);
        var ts = TimeSpan.FromSeconds(whole);
        var head = ts.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{ts.Minutes}:{ts.Seconds:00}");
        if (step >= 1) return head;
        var frac = seconds - whole;
        var digits = step >= 0.1 ? 1 : 2;
        var f = Math.Round(frac * Math.Pow(10, digits));
        return head + "." + f.ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    // accepts 12, 12.5, 1:02.3, 1:02:03.25 (also comma decimals)
    public static double? Parse(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return null;
        var parts = text.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = total * 60 + v;
        }
        return total;
    }
}
