using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioCut;

public static class TimeFormat
{
    /// <summary>01:23.456 or 1:02:03.456</summary>
    public static string Full(double s)
    {
        s = Math.Max(0, s);
        var ts = TimeSpan.FromMilliseconds(Math.Round(s * 1000));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    /// <summary>1:23.4</summary>
    public static string Short(double s)
    {
        s = Math.Max(0, s);
        var ts = TimeSpan.FromMilliseconds(Math.Round(s * 10) * 100);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}"
            : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    public static string Ruler(double t, double step)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Round(t * 1000));
        string hm = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}" : $"{ts.Minutes}";
        if (step >= 1) return $"{hm}:{ts.Seconds:00}";
        if (step >= 0.1) return $"{hm}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        return $"{hm}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }

    /// <summary>Human friendly length: "12,3 s", "3 min 05 s", "1 h 02 min".</summary>
    public static string Length(double s)
    {
        if (s < 60) return s.ToString("0.0", CultureInfo.GetCultureInfo("pl-PL")) + " s";
        var ts = TimeSpan.FromSeconds(Math.Round(s));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} h {ts.Minutes:00} min" : $"{ts.Minutes} min {ts.Seconds:00} s";
    }

    /// <summary>For file names: 01-23 or 1-02-03.</summary>
    public static string FileSafe(double s)
    {
        var ts = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, s)));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}-{ts.Minutes:00}-{ts.Seconds:00}" : $"{ts.Minutes:00}-{ts.Seconds:00}";
    }

    /// <summary>Accepts "83", "83.5", "1:23", "1:23.5", "1:02:03,25".</summary>
    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        var m = Regex.Match(text.Trim().Replace(',', '.'), @"^(?:(?:(\d+):)?(\d+):)?(\d+(?:\.\d*)?)$");
        if (!m.Success) return false;
        double h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        double min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        double sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        seconds = h * 3600 + min * 60 + sec;
        return true;
    }
}
