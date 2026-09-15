using System.IO.Compression;
using System.Text;

namespace Piper.App;

/// <summary>
/// Builds the zip a user attaches to a bug report.
/// </summary>
/// <remarks>
/// The contents are deliberately limited to Piper's own log, its crash log and a summary of the
/// machine. Captured traffic, bodies, cookies, certificates and proxy configuration are never
/// included: a diagnostics file is forwarded to strangers by definition, so anything that lands in
/// it has left the user's control. Adding a new entry here means deciding that its contents are
/// safe to publish.
/// </remarks>
internal static class DiagnosticsBundle
{
    /// <summary>
    /// Upper bound on the crash log copied into the bundle. The file accumulates across every run
    /// on the machine and is never truncated, so only its tail - the runs that matter - is taken.
    /// </summary>
    private const int MaxCrashLogBytes = 512 * 1024;

    /// <summary>Writes the bundle into <paramref name="destination"/>, which is left open.</summary>
    public static void Write(Stream destination, string environment, string log, string? crashLogPath)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        AddText(archive, "environment.txt", environment);
        AddText(archive, "log.txt", log);
        if (crashLogPath is not null) AddText(archive, "crash.log", ReadCrashLogTail(crashLogPath));
    }

    /// <summary>Entries the bundle always contains, for telling the user what they are sending.</summary>
    public static string Contents => "environment.txt, log.txt and crash.log (when one exists)";

    private static void AddText(ZipArchive archive, string name, string? content)
    {
        if (content is null) return;

        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    /// <summary>
    /// The tail of the crash log, or null when there is nothing to report. A crash log that cannot
    /// be read must not cost the user the rest of the bundle, so the failure is recorded as the
    /// entry's content instead of being thrown.
    /// </summary>
    private static string? ReadCrashLogTail(string path)
    {
        try
        {
            // FileShare.ReadWrite because the running process appends to this same file.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length == 0) return null;

            var truncated = file.Length > MaxCrashLogBytes;
            if (truncated) file.Seek(-MaxCrashLogBytes, SeekOrigin.End);

            using var reader = new StreamReader(file, Encoding.UTF8);
            var text = reader.ReadToEnd();
            return truncated ? $"[earlier entries omitted]{Environment.NewLine}{text}" : text;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"crash log at {path} could not be read: {ex.Message}";
        }
    }
}
