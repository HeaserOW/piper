using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Piper.App;

/// <summary>
/// Looks translations up from <c>Locales/en.json</c>, following the i18next JSON v4 conventions.
/// </summary>
/// <remarks>
/// Supported, matching https://www.i18next.com/misc/json-format:
/// <list type="bullet">
/// <item>Nested objects addressed by a dotted key -- <c>T("menu.file")</c>.</item>
/// <item>Interpolation -- <c>"replace this {{value}}"</c>.</item>
/// <item>Formatting -- <c>"{{count, N0}}"</c>. i18next names a registered formatter there; the
/// formatter here is .NET formatting, so the name <em>is</em> the format specifier and <c>N0</c>,
/// <c>HH:mm:ss</c> and <c>X</c> all work. Values format under
/// <see cref="CultureInfo.CurrentCulture"/>, as the interpolated strings they replaced did.</item>
/// <item>Nesting -- <c>"$t(common.allFilesFilter)"</c> splices another entry in, which is what the
/// previous <c>const</c> concatenation did.</item>
/// <item>Plurals -- <c>key_one</c> / <c>key_other</c>, selected by an argument named <c>count</c>.
/// English only: a locale with more categories needs a CLDR plural-rule table here.</item>
/// </list>
///
/// Deliberately not supported: i18next escapes interpolated values for the DOM and offers
/// <c>{{- value}}</c> to opt out. There is no markup in WinForms, so nothing is escaped and a
/// captured header or URL reaches a label as the bytes it arrived as.
///
/// The catalogue is an embedded resource rather than a file beside the exe. It is therefore part
/// of the assembly and not an external file that could be edited, replaced, or truncated between
/// builds -- a translation file read from disk would be attacker-controlled input on a shared
/// machine, and every string here ends up in a dialog, a menu, or a log line.
///
/// The capture grid resolves several entries per visible row on every refresh, so substitution
/// runs through <see cref="DefaultInterpolatedStringHandler"/> -- the same pooled machinery the
/// compiler emits for an interpolated string -- rather than a <see cref="StringBuilder"/>, and a
/// plain label is handed back from the cache without touching either.
/// </remarks>
internal static class I18n
{
    /// <summary>Caps <c>$t()</c> recursion so a catalogue that referenced itself cannot hang the UI.</summary>
    private const int MaxNestingDepth = 8;

    private static readonly Dictionary<string, string> Catalogue = Load();

    /// <summary>
    /// Templates parsed on first use. Row rendering asks for the same handful of keys several times
    /// a second, so re-scanning a template per call would put string parsing on the paint path.
    /// Concurrent because update checks resolve their own messages on a worker thread while the UI
    /// thread is drawing.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Template> Templates = new(StringComparer.Ordinal);

    /// <summary>Every key in the catalogue. The smoke tests check the typed accessors against it.</summary>
    public static IReadOnlyCollection<string> Keys => Catalogue.Keys;

    /// <summary>
    /// The placeholder names an entry substitutes. The smoke tests use this to prove every accessor
    /// supplies all of them: a name that does not match silently renders as nothing, which is the
    /// one way this design can drop text without failing.
    /// </summary>
    public static IEnumerable<string> Placeholders(string key) =>
        Catalogue.TryGetValue(key, out var text)
            ? Compile(text, depth: 0).Segments.Where(s => s.Name is not null).Select(s => s.Name!)
            : [];

    /// <summary>
    /// The translation for <paramref name="key"/>, with <paramref name="values"/> interpolated.
    /// Returns the key itself when it is missing, as i18next does; the smoke tests fail the build
    /// long before that could reach a user.
    /// </summary>
    public static string T(string key, params ReadOnlySpan<(string Name, object? Value)> values)
    {
        var resolved = Plural(key, values);
        if (!Templates.TryGetValue(resolved, out var template))
        {
            if (!Catalogue.TryGetValue(resolved, out var text)) return key;
            template = Templates.GetOrAdd(resolved, Compile(text, depth: 0));
        }

        // A plain label -- most of the catalogue -- needs no work at all.
        if (template.Constant is { } constant) return constant;

        var handler = new DefaultInterpolatedStringHandler(
            template.LiteralLength, template.PlaceholderCount, CultureInfo.CurrentCulture);
        foreach (var segment in template.Segments)
        {
            if (segment.Name is null) handler.AppendLiteral(segment.Text!);
            else handler.AppendFormatted(Value(values, segment.Name), segment.Format);
        }

        return handler.ToStringAndClear();
    }

    /// <summary>
    /// Appends the i18next plural suffix when the caller passed a <c>count</c>. English has the two
    /// categories "one" and "other"; anything that is not exactly one takes "other".
    /// </summary>
    private static string Plural(string key, ReadOnlySpan<(string Name, object? Value)> values)
    {
        foreach (var (name, value) in values)
        {
            if (!string.Equals(name, "count", StringComparison.Ordinal)) continue;

            var suffixed = key + (IsOne(value) ? "_one" : "_other");
            return Catalogue.ContainsKey(suffixed) ? suffixed : key;
        }

        return key;
    }

    private static bool IsOne(object? value) => value switch
    {
        int i => i == 1,
        long l => l == 1,
        double d => d == 1,
        _ => false,
    };

    private static object? Value(ReadOnlySpan<(string Name, object? Value)> values, string name)
    {
        foreach (var (candidate, value) in values)
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return value;
        return null;
    }

    /// <summary>A literal run of text, or one <c>{{name}}</c> / <c>{{name, format}}</c> placeholder.</summary>
    /// <remarks>
    /// <paramref name="Format"/> is the bare .NET format specifier -- "N0", not "{0:N0}" -- which is
    /// what <see cref="DefaultInterpolatedStringHandler.AppendFormatted{T}(T, string)"/> wants, so
    /// nothing has to parse a composite format string per call.
    /// </remarks>
    private readonly record struct Segment(string? Text, string? Name, string? Format);

    /// <summary>
    /// A parsed entry. The two counts size the handler's buffer in one go, and
    /// <paramref name="Constant"/> is set for an entry that substitutes nothing.
    /// </summary>
    private sealed record Template(Segment[] Segments, int LiteralLength, int PlaceholderCount, string? Constant);

    /// <summary>
    /// Splits a template into literal runs and placeholders, splicing in any <c>$t(other.key)</c>
    /// reference as it goes. A lone brace is literal text, so a message may contain one.
    /// </summary>
    private static Template Compile(string template, int depth)
    {
        var segments = new List<Segment>();
        var literal = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '$' && depth < MaxNestingDepth && Nested(template, i) is { } reference)
            {
                // Spliced at compile time: a referenced entry cannot depend on this call's
                // arguments, so there is nothing to defer to substitution time. Its parsed segments
                // are taken as they are -- rendering it back to text and re-scanning would have to
                // reproduce every placeholder's format specifier exactly, and quietly dropped it.
                if (Catalogue.TryGetValue(reference.Key, out var value))
                {
                    if (literal.Length > 0)
                    {
                        segments.Add(new Segment(literal.ToString(), null, null));
                        literal.Clear();
                    }

                    segments.AddRange(Compile(value, depth + 1).Segments);
                }
                else
                {
                    literal.Append(reference.Key);
                }

                i = reference.After - 1;
                continue;
            }

            if (template[i] != '{' || i + 1 >= template.Length || template[i + 1] != '{')
            {
                literal.Append(template[i]);
                continue;
            }

            var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                literal.Append(template[i]);
                continue;
            }

            if (literal.Length > 0)
            {
                segments.Add(new Segment(literal.ToString(), null, null));
                literal.Clear();
            }

            var body = template[(i + 2)..close];
            var comma = body.IndexOf(',');
            segments.Add(comma < 0
                ? new Segment(null, body.Trim(), null)
                : new Segment(null, body[..comma].Trim(), body[(comma + 1)..].Trim()));
            i = close + 1;
        }

        if (literal.Length > 0 || segments.Count == 0)
            segments.Add(new Segment(literal.ToString(), null, null));

        var literalLength = segments.Where(s => s.Name is null).Sum(s => s.Text!.Length);
        var placeholders = segments.Count(s => s.Name is not null);
        return new Template([.. segments], literalLength, placeholders,
            placeholders == 0 ? string.Concat(segments.Select(s => s.Text)) : null);
    }

    /// <summary>The key inside a <c>$t(...)</c> reference at <paramref name="start"/>, or null.</summary>
    private static (string Key, int After)? Nested(string template, int start)
    {
        if (start + 3 >= template.Length || template[start + 1] != 't' || template[start + 2] != '(') return null;

        var close = template.IndexOf(')', start + 3);
        return close < 0 ? null : (template[(start + 3)..close].Trim(), close + 1);
    }

    /// <summary>
    /// Reads the embedded catalogue into dotted keys. Line endings are normalised to CRLF on the
    /// way in, so a multi-line message written with plain "\n" in JSON still reaches a Windows
    /// message box and the log view correctly.
    /// </summary>
    private static Dictionary<string, string> Load()
    {
        const string resource = "Piper.App.Locales.en.json";
        using var stream = typeof(I18n).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"The string catalogue '{resource}' is missing from {typeof(I18n).Assembly.GetName().Name}.");

        using var document = JsonDocument.Parse(stream);
        var catalogue = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(document.RootElement, prefix: string.Empty, catalogue);
        return catalogue;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, into);
                    break;
                case JsonValueKind.String:
                    // JSON allows a duplicate name and System.Text.Json keeps the last one, so an
                    // entry pasted twice would silently shadow the one above it.
                    if (!into.TryAdd(key, property.Value.GetString()!.ReplaceLineEndings("\r\n")))
                        throw new InvalidOperationException($"The string catalogue defines '{key}' more than once.");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The string catalogue entry '{key}' is a {property.Value.ValueKind}; "
                        + "only objects and strings are supported.");
            }
        }
    }
}
