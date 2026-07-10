using System.Globalization;

namespace Cut.Core;

public static class TimeFmt
{
    // 0:04.20, 12:34.56, 1:02:03.40
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        var cs = (int)(seconds * 100) % 100;
        return ts.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{cs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{ts.Minutes}:{ts.Seconds:00}.{cs:00}");
    }

    // ruler labels: 0:05, 1:30, 1:02:03; sub-second steps get one decimal
    public static string Ruler(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        if (seconds != Math.Floor(seconds))
            return string.Create(CultureInfo.InvariantCulture, $"{ts.Minutes}:{ts.Seconds + ts.Milliseconds / 1000.0:00.0}");
        return ts.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{ts.Minutes}:{ts.Seconds:00}");
    }
}
