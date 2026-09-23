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

    /// <summary>Trims "application/json; charset=utf-8" down to "json" for grid display.</summary>
    public static string ShortContentType(string? contentType) => MimeTypes.ShortName(contentType);
}
