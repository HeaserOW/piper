using System.IO.Compression;
using System.Text;

namespace Piper.App;

/// <summary>
/// Builds the zip a user attaches to a bug report, and owns the shape of the log that goes in it.
/// </summary>
/// <remarks>
/// The entry list is deliberately short: Piper's own log, its crash log and a summary of the
/// machine. Captured traffic, bodies, cookies, certificates and proxy configuration are never
/// included.
///
/// That allowlist is per file, not per line. <c>log.txt</c> is whatever the Log tab holds, so the
/// guarantee is only as strong as the call sites feeding it -- see the note on
/// <c>MainForm.AppendLog</c>. <see cref="SanitizeLogMessage"/> is the one thing enforced for every
/// line, and it covers the two problems a call site cannot be trusted to remember: a local path
/// naming the Windows account, and text taken off a drag or an exception that could otherwise forge
/// whole lines in a file the user forwards as evidence.
/// </remarks>
internal static class DiagnosticsBundle
{
    /// <summary>
    /// Upper bound on the crash log copied into the bundle. The file accumulates across every run
    /// on the machine and is never truncated, so only its tail - the runs that matter - is taken.
    /// </summary>
    private const int MaxCrashLogBytes = 512 * 1024;

    private static readonly string UserProfileDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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

    /// <summary>
    /// A log message made safe to publish: the user's profile directory reduced to
    /// <c>%USERPROFILE%</c> so the bundle does not carry the Windows account name, and control
    /// characters flattened so that no value interpolated into a message - a dropped file name, a
    /// clipboard format, an exception text - can forge additional timestamped lines.
    /// </summary>
    public static string SanitizeLogMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var redacted = Redact(message);

        // One predicate decides both whether to rewrite and what to rewrite. A hand-listed set of
        // control characters here would silently pass through whatever it forgot - C1, DEL, U+0085
        // - which is the opposite of what a sanitiser is for.
        if (!HasControlCharacter(redacted)) return redacted;
        return string.Create(redacted.Length, redacted, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
                span[index] = char.IsControl(source[index]) ? ' ' : source[index];
        });
    }

    /// <summary>
    /// Text with the user's profile directory reduced to <c>%USERPROFILE%</c>. Used on its own for
    /// content whose line breaks must survive, such as the crash log.
    /// </summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return UserProfileDirectory.Length > 0
            ? text.Replace(UserProfileDirectory, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            : text;
    }

    private static bool HasControlCharacter(string text)
    {
        foreach (var character in text)
            if (char.IsControl(character)) return true;
        return false;
    }

    /// <summary>
    /// The newest half of a log that has outgrown <paramref name="cap"/>, starting at a line
    /// boundary. Trimming beats clearing: a session long enough to reach the cap is exactly the one
    /// whose log is worth exporting.
    /// </summary>
    public static string TrimToNewestLines(string log, int cap)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (log.Length <= cap) return log;

        var kept = log[(log.Length - (cap / 2))..];
        var firstLine = kept.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        return firstLine < 0 ? kept : kept[(firstLine + Environment.NewLine.Length)..];
    }

    /// <summary>
    /// Up to <paramref name="max"/> items, with a count standing in for the rest of
    /// <paramref name="total"/>. Anything taken off a drop is unbounded in count, and one log line
    /// naming every file in a dropped folder would blow the log cap on its own.
    /// </summary>
    /// <remarks>
    /// Takes the total separately so the caller can pass a lazy projection: only the items that
    /// will be printed are evaluated, which is what keeps a per-item filesystem probe off the whole
    /// dropped set.
    /// </remarks>
    public static string Summarise(IEnumerable<string> items, int total, int max)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (total == 0) return "none";

        var shown = string.Join(", ", items.Take(max));
        return total <= max ? shown : $"{shown} and {total - max} more";
    }

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

            // Bounded by the read, not by the seek: the file is shared with a writer that may
            // append between measuring its length and reading it, and reading to EOF would then
            // copy more than the cap allows.
            var buffer = new byte[MaxCrashLogBytes];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            // Redacted like every log line: a stack trace carries the paths it was thrown from,
            // and the bundle promises the account name is not in it. Control characters are left
            // alone here - a crash log is many lines by nature, and nothing interpolates into it.
            var text = Redact(Encoding.UTF8.GetString(buffer, 0, read));
            if (!truncated) return text;

            // A tail that starts mid-line also starts mid-UTF-8-sequence, which decodes to a
            // replacement character. Dropping the partial line removes both.
            var firstLine = text.IndexOf('\n');
            return $"[earlier entries omitted]{Environment.NewLine}"
                + (firstLine < 0 ? text : text[(firstLine + 1)..]);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File name only: the bundle is forwarded by definition, so it carries no more of the
            // machine's directory layout than it has to.
            return $"crash log {Path.GetFileName(path)} could not be read: {ex.Message}";
        }
    }
}
