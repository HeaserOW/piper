using Piper.Core.Http;

namespace Piper.App.Controls;

/// <summary>Display formatting shared by the grid, the inspector and the composer.</summary>
internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => Strings.Units.NoBytes,
        < 1024 => Strings.Units.Bytes(bytes),
        < 1024 * 1024 => Strings.Units.Kilobytes(bytes / 1024.0),
        _ => Strings.Units.Megabytes(bytes / (1024.0 * 1024)),
    };

    private const double Kilobyte = 1024, Megabyte = Kilobyte * 1024, Gigabyte = Megabyte * 1024;

    /// <summary>
    /// "3.0/8.0 MB" for a body still arriving, in the unit of its total so the two line up. The
    /// received figure is rounded down, so a body that is not finished never reads as finished.
    /// </summary>
    public static string SizeProgress(long received, long total) => total switch
    {
        < (long)Kilobyte => Strings.Units.ProgressBytes(received, total),
        < (long)Megabyte => Strings.Units.ProgressKilobytes(Floor(received / Kilobyte, 1), total / Kilobyte),
        < (long)Gigabyte => Strings.Units.ProgressMegabytes(Floor(received / Megabyte, 1), total / Megabyte),
        _ => Strings.Units.ProgressGigabytes(Floor(received / Gigabyte, 2), total / Gigabyte),
    };

    /// <summary>"3.02 MB of 8.00 MB (37%)", or just what has arrived when the total is unknown.</summary>
    public static string ProgressDetail(long received, long total) => total > 0
        ? Strings.Units.OfTotal(
            received > 0 ? Size(received) : Strings.Units.Bytes(0), Size(total),
            Floor(Math.Clamp((double)received / total, 0, 1), 2))
        : Size(received);

    private static double Floor(double value, int decimals)
    {
        var scale = Math.Pow(10, decimals);
        return Math.Floor(value * scale) / scale;
    }

    /// <summary>Trims "application/json; charset=utf-8" down to "json" for grid display.</summary>
    public static string ShortContentType(string? contentType) => MimeTypes.ShortName(contentType);
}
