using System.Globalization;

namespace StorageRescue.Core;

public static class Units
{
    static readonly string[] Si = { "B", "kB", "MB", "GB", "TB", "PB" };

    /// <summary>ddrescue と同じ SI 単位表記（1 kB = 1000 B）</summary>
    public static string Bytes(double v)
    {
        int i = 0;
        while (Math.Abs(v) >= 1000 && i < Si.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{v:0} {Si[i]}" : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", v, Si[i]);
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
