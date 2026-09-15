using System.Text.RegularExpressions;
using Piper.App;

/// <summary>
/// Guards the rules that keep <c>Locales/en.json</c> usable as the string catalogue: every key the
/// typed accessors ask for exists, every key in the file is still reached from code, and no menu
/// label smuggles its accelerator into its own text.
/// </summary>
internal static partial class UiStringTests
{
    /// <summary>Matches the key literal in <c>I18n.T("some.key"</c>. The character class stops at
    /// the closing quote, so the pattern does not need to include it.</summary>
    [GeneratedRegex("""I18n\.T\("([A-Za-z0-9_.]+)""")]
    private static partial Regex KeyReference();

    public static Task RunAsync(TestRunner runner) => runner.RunAsync("UI string catalogue", () =>
    {
        // A tab in a ToolStripMenuItem's Text is the Win32 menu convention, which WinForms does not
        // implement: it renders as nothing, so "Resend request\tCtrl+R" reached the screen as
        // "Resend requestCtrl+R". Accelerators belong in ShortcutKeyDisplayString, which is what
        // Menus.Item sets, so no UI string in Piper.App may carry one.
        var tabbed = SourceLinesWithTabEscape().ToList();
        runner.AreEqual(0, tabbed.Count,
            tabbed.Count == 0
                ? "no menu label embeds its accelerator with a tab"
                : "menu labels embed an accelerator with a tab: " + string.Join("; ", tabbed));

        var catalogue = I18n.Keys.ToHashSet(StringComparer.Ordinal);
        var referenced = ReferencedKeys();

        var missing = referenced.Where(key => !Exists(catalogue, key)).Order(StringComparer.Ordinal).ToList();
        runner.AreEqual(0, missing.Count,
            missing.Count == 0
                ? "every key the accessors ask for is in en.json"
                : "keys missing from en.json: " + string.Join(", ", missing));

        // Catches the other half of a rename: an entry left behind in the JSON that nothing reads
        // any more, which a translator would otherwise keep paying to translate.
        var orphans = catalogue.Select(Base).Distinct(StringComparer.Ordinal)
            .Where(key => !referenced.Contains(key)).Order(StringComparer.Ordinal).ToList();
        runner.AreEqual(0, orphans.Count,
            orphans.Count == 0
                ? "every entry in en.json is still reached from code"
                : "unreferenced entries in en.json: " + string.Join(", ", orphans));

        // Only entries that substitute nothing: one that is entirely a placeholder renders empty
        // here purely because this check passes no arguments.
        var blank = catalogue
            .Where(key => !I18n.Placeholders(key).Any() && I18n.T(key).Length == 0)
            .Order(StringComparer.Ordinal).ToList();
        runner.AreEqual(0, blank.Count,
            blank.Count == 0 ? "no entry is empty" : "empty entries: " + string.Join(", ", blank));

        // The one way this design can lose text without failing: a placeholder the accessor never
        // supplies renders as nothing, so "Root CA: {{path}}" would quietly become "Root CA: ".
        var unsupplied = UnsuppliedPlaceholders(catalogue).ToList();
        runner.AreEqual(0, unsupplied.Count,
            unsupplied.Count == 0
                ? "every accessor supplies every placeholder its entry substitutes"
                : "placeholders no accessor supplies: " + string.Join("; ", unsupplied));

        // The i18next features the catalogue actually relies on.
        runner.AreEqual("Root CA: C:/x.pfx", Strings.Log.RootCaPath("C:/x.pfx"), "{{value}} interpolation");
        runner.AreEqual("found 0xFF", Strings.Inspector.FoundAt(255), "{{value, format}} formatting");
        runner.AreEqual("1 match", Strings.Inspector.MatchCount(1), "the _one plural form");
        runner.AreEqual("4 matches", Strings.Inspector.MatchCount(4), "the _other plural form");
        runner.AreEqual("0 matches", Strings.Inspector.MatchCount(0), "zero takes the _other form in English");
        runner.IsTrue(Strings.Filters.FilterSetFilter.EndsWith(Strings.Common.AllFilesFilter, StringComparison.Ordinal),
            "$t(common.allFilesFilter) splices the shared entry in");
        // A spliced entry has to keep its format specifier, or "$t(sessionList.duration)" would
        // render "1234 ms" where the entry it borrows asks for "1,234 ms".
        runner.AreEqual("   total " + Strings.SessionList.Duration(1234), Strings.Inspector.TimingTotal(1234),
            "$t() carries the borrowed entry's format specifier through");
        runner.AreEqual("piper.no.such.key", I18n.T("piper.no.such.key"), "a missing key falls back to itself");

        return Task.CompletedTask;
    });

    /// <summary>True when the key is present directly, or as its two English plural forms.</summary>
    private static bool Exists(HashSet<string> catalogue, string key) =>
        catalogue.Contains(key) || (catalogue.Contains(key + "_one") && catalogue.Contains(key + "_other"));

    /// <summary>A plural entry's key without its category suffix.</summary>
    private static string Base(string key) =>
        key.EndsWith("_one", StringComparison.Ordinal) ? key[..^4]
        : key.EndsWith("_other", StringComparison.Ordinal) ? key[..^6]
        : key;

    /// <summary>Every key literal passed to <c>I18n.T</c> anywhere in <c>src/Piper.App</c>.</summary>
    private static HashSet<string> ReferencedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in AppSources())
            foreach (Match match in KeyReference().Matches(File.ReadAllText(file)))
                keys.Add(match.Groups[1].Value);
        return keys;
    }

    /// <summary>
    /// Reports "entry -> placeholder" for every placeholder the accessor behind it never passes.
    /// </summary>
    private static IEnumerable<string> UnsuppliedPlaceholders(HashSet<string> catalogue)
    {
        foreach (var file in AppSources())
        {
            foreach (var (key, supplied) in Calls(File.ReadAllText(file)))
            {
                // A counted entry exists only under its plural suffixes; both forms are rendered
                // with the same arguments, so both have to be satisfied.
                foreach (var entry in new[] { key, key + "_one", key + "_other" })
                {
                    if (!catalogue.Contains(entry)) continue;
                    foreach (var placeholder in I18n.Placeholders(entry).Distinct(StringComparer.Ordinal))
                        if (!supplied.Contains(placeholder))
                            yield return $"{entry} -> {{{{{placeholder}}}}}";
                }
            }
        }
    }

    /// <summary>
    /// Each <c>I18n.T(...)</c> call, as its key and the argument names it passes.
    /// </summary>
    /// <remarks>
    /// Scans rather than reflects because the argument names are literals in the call, not
    /// parameter names. Depth tracking keeps a nested <c>I18n.T(...)</c> in an argument from having
    /// its names attributed to the outer call; it gets its own entry when the scan reaches it.
    /// Safe against parentheses inside literals because every literal in these calls is a key or an
    /// argument name, and both are bare identifiers.
    /// </remarks>
    private static IEnumerable<(string Key, HashSet<string> Supplied)> Calls(string source)
    {
        const string marker = "I18n.T(";
        for (var start = source.IndexOf(marker, StringComparison.Ordinal); start >= 0;
             start = source.IndexOf(marker, start + marker.Length, StringComparison.Ordinal))
        {
            var supplied = new HashSet<string>(StringComparer.Ordinal);
            string? key = null;
            var depth = 1;

            for (var i = start + marker.Length; i < source.Length && depth > 0; i++)
            {
                switch (source[i])
                {
                    case '(' when depth == 1 && Literal(source, i + 1) is { } name:
                        supplied.Add(name);
                        depth++;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case '"' when depth == 1 && key is null:
                        key = Literal(source, i);
                        break;
                }
            }

            if (key is not null) yield return (key, supplied);
        }
    }

    /// <summary>The contents of the identifier-shaped string literal at <paramref name="at"/>, or null.</summary>
    private static string? Literal(string source, int at)
    {
        if (at >= source.Length || source[at] != '"') return null;
        var end = source.IndexOf('"', at + 1);
        if (end < 0) return null;

        var value = source[(at + 1)..end];
        return value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.') ? value : null;
    }

    /// <summary>
    /// Source lines in <c>src/Piper.App</c> that contain a <c>\t</c> escape outside a comment.
    /// Read from the repository rather than from the compiled assembly: the offending text was a
    /// literal at a call site, not a value this test project ever links.
    /// </summary>
    private static IEnumerable<string> SourceLinesWithTabEscape()
    {
        foreach (var file in AppSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                // \\ is an escaped backslash, not the accelerator separator this rule is about.
                if (!line.Replace("\\\\", string.Empty).Contains("\\t", StringComparison.Ordinal)) continue;
                yield return $"{Path.GetFileName(file)}:{i + 1}";
            }
        }
    }

    private static IEnumerable<string> AppSources()
    {
        var app = Path.Combine(RepositoryRoot(), "src", "Piper.App");
        // obj/ holds generated sources that are not ours to police.
        return Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = AppContext.BaseDirectory; directory is not null;
             directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)))
        {
            if (File.Exists(Path.Combine(directory, "Piper.slnx"))) return directory;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory + ".");
    }
}
