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
            // Well past the 512 KB cap, and with a marker at each end so truncation direction shows.
            File.WriteAllText(crashLog, "FIRST" + new string('x', 4 * 1024 * 1024) + "LAST");

            using var stream = new MemoryStream();
            DiagnosticsBundle.Write(stream, "environment", "log", crashLog);

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var copied = ReadEntry(archive, "crash.log")!;
            runner.IsTrue(copied.Length < 600 * 1024, "an unbounded crash log cannot bloat the bundle");
            runner.IsTrue(copied.EndsWith("LAST", StringComparison.Ordinal), "the newest entries are the ones kept");
            runner.IsTrue(!copied.Contains("FIRST", StringComparison.Ordinal), "the oldest entries are dropped");
        }
        finally
        {
            if (File.Exists(crashLog)) File.Delete(crashLog);
        }

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
