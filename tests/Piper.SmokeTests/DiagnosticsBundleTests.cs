using System.IO.Compression;
using System.Text;
using Piper.App;

internal static class DiagnosticsBundleTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("diagnostics bundle carries the log and nothing else", () =>
    {
        var crashLog = Path.Combine(Path.GetTempPath(), $"piper-crash-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(crashLog, "InvalidOperationException: boom");

            using var stream = new MemoryStream();
            DiagnosticsBundle.Write(stream, "Piper 0.5.0, elevated: no", "12:00:00  Ignored a drop", crashLog);

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray();
            runner.AreEqual("crash.log, environment.txt, log.txt", string.Join(", ", names),
                "bundle holds the three diagnostic entries and no captured traffic");
            runner.AreEqual("12:00:00  Ignored a drop", ReadEntry(archive, "log.txt"), "log is copied verbatim");
            runner.AreEqual("Piper 0.5.0, elevated: no", ReadEntry(archive, "environment.txt"), "environment is copied verbatim");
            runner.AreEqual("InvalidOperationException: boom", ReadEntry(archive, "crash.log"), "crash log is copied verbatim");
        }
        finally
        {
            if (File.Exists(crashLog)) File.Delete(crashLog);
        }

        return Task.CompletedTask;
    });

    public static Task RunMissingCrashLogAsync(TestRunner runner) => runner.RunAsync("diagnostics bundle survives a missing crash log", () =>
    {
        var absent = Path.Combine(Path.GetTempPath(), $"piper-absent-{Guid.NewGuid():N}.log");

        using var stream = new MemoryStream();
        DiagnosticsBundle.Write(stream, "environment", "log", absent);

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        runner.AreEqual("environment.txt, log.txt",
            string.Join(", ", archive.Entries.Select(entry => entry.FullName).OrderBy(name => name)),
            "a crash log that was never written leaves no empty entry behind");

        return Task.CompletedTask;
    });

    public static Task RunHugeCrashLogAsync(TestRunner runner) => runner.RunAsync("diagnostics bundle bounds the crash log it copies", () =>
    {
        var crashLog = Path.Combine(Path.GetTempPath(), $"piper-crash-{Guid.NewGuid():N}.log");
        try
        {
            // Well past the 512 KB cap, in lines, with a marker at each end so the truncation
            // direction shows. A multi-byte character on every line means a tail that started at
            // an arbitrary byte offset would decode with a replacement character at the front.
            var body = string.Concat(Enumerable.Repeat($"OLDER ééé entry{Environment.NewLine}", 40_000));
            File.WriteAllText(crashLog, $"FIRST{Environment.NewLine}{body}LAST");

            using var stream = new MemoryStream();
            DiagnosticsBundle.Write(stream, "environment", "log", crashLog);

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var copied = ReadEntry(archive, "crash.log")!;
            runner.IsTrue(Encoding.UTF8.GetByteCount(copied) <= (512 * 1024) + 64,
                "an unbounded crash log cannot bloat the bundle beyond the cap plus its notice");
            runner.IsTrue(copied.EndsWith("LAST", StringComparison.Ordinal), "the newest entries are the ones kept");
            runner.IsTrue(!copied.Contains("FIRST", StringComparison.Ordinal), "the oldest entries are dropped");
            runner.IsTrue(copied.StartsWith($"[earlier entries omitted]{Environment.NewLine}OLDER", StringComparison.Ordinal),
                "the copy resumes on a whole line, so no split UTF-8 sequence is left at the front");
            runner.IsTrue(!copied.Contains('�'), "no replacement character survives the byte-offset seek");
        }
        finally
        {
            if (File.Exists(crashLog)) File.Delete(crashLog);
        }

        return Task.CompletedTask;
    });

    public static Task RunLogSanitisingAsync(TestRunner runner) => runner.RunAsync("exported log messages cannot carry the account name or forge lines", () =>
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        runner.AreEqual(@"Root CA: %USERPROFILE%\AppData\Local\Piper\Piper-Root.pfx",
            DiagnosticsBundle.SanitizeLogMessage($@"Root CA: {profile}\AppData\Local\Piper\Piper-Root.pfx"),
            "the Windows account name is replaced by %USERPROFILE%");
        runner.AreEqual("%USERPROFILE%",
            DiagnosticsBundle.SanitizeLogMessage(profile.ToUpperInvariant()),
            "the profile path is matched however Windows cased it");

        // A drag source picks the file names and clipboard formats that land in the log, so a
        // newline there would let it write whole timestamped lines into the exported evidence.
        runner.AreEqual("evil.txt  12:00:00  Capture stopped",
            DiagnosticsBundle.SanitizeLogMessage("evil.txt\r\n12:00:00  Capture stopped"),
            "CR/LF in an interpolated value cannot start a new log line");
        runner.AreEqual("a b c", DiagnosticsBundle.SanitizeLogMessage("a\tbc"),
            "tabs and escapes are flattened too");
        runner.AreEqual("nothing to do", DiagnosticsBundle.SanitizeLogMessage("nothing to do"),
            "an ordinary message is returned unchanged");

        return Task.CompletedTask;
    });

    public static Task RunLogTrimmingAsync(TestRunner runner) => runner.RunAsync("the log keeps its newest whole lines at the cap", () =>
    {
        runner.AreEqual("short", DiagnosticsBundle.TrimToNewestLines("short", 100),
            "a log under the cap is untouched");

        var line = $"12:00:00  a message{Environment.NewLine}";
        var log = string.Concat(Enumerable.Repeat(line, 400));
        var trimmed = DiagnosticsBundle.TrimToNewestLines(log, 1_000);

        runner.IsTrue(trimmed.Length <= 1_000 / 2, "the trimmed log is no larger than half the cap");
        runner.IsTrue(trimmed.StartsWith("12:00:00", StringComparison.Ordinal),
            "trimming resumes at a line boundary, never mid-line");
        runner.IsTrue(trimmed.EndsWith(line, StringComparison.Ordinal), "the newest line survives");

        // AppendLog terminates every line, so a log with no break at all only happens if one
        // message is longer than the cap. That must stay bounded rather than wedging the logger,
        // and keeping the newest fragment beats discarding the message entirely.
        var oneHugeLine = new string('x', 5_000);
        var fragment = DiagnosticsBundle.TrimToNewestLines(oneHugeLine, 1_000);
        runner.AreEqual(500, fragment.Length, "a log with no line break is cut to half the cap, not dropped");

        return Task.CompletedTask;
    });

    public static Task RunSummarisingAsync(TestRunner runner) => runner.RunAsync("logged name lists are bounded", () =>
    {
        runner.AreEqual("none", DiagnosticsBundle.Summarise([], 0, 3), "an empty list says so");
        runner.AreEqual("a, b", DiagnosticsBundle.Summarise(["a", "b"], 2, 3), "a short list is printed whole");
        runner.AreEqual("a, b, c", DiagnosticsBundle.Summarise(["a", "b", "c"], 3, 3), "a list at the cap has no suffix");
        runner.AreEqual("a, b, c and 997 more", DiagnosticsBundle.Summarise(["a", "b", "c", "d"], 1_000, 3),
            "the remainder is counted from the real total, not the items printed");

        // The drop handler probes the filesystem inside this projection, so anything past the cap
        // must never be evaluated.
        var evaluated = 0;
        var lazy = Enumerable.Range(0, 1_000).Select(index => { evaluated++; return index.ToString(); });
        _ = DiagnosticsBundle.Summarise(lazy, 1_000, 3);
        runner.AreEqual(3, evaluated, "only the items that get printed are evaluated");

        return Task.CompletedTask;
    });

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
