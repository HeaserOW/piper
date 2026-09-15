using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>Which host groups the user has folded away in the Composer history pane.</summary>
/// <remarks>
/// Only collapsed hosts are recorded, so a missing, unreadable, or truncated file means "everything
/// expanded" - the behaviour the pane had before groups existed, and the right default for a host
/// the user has never seen.
/// </remarks>
public sealed class ComposerViewState
{
    public List<string> CollapsedHosts { get; set; } = [];
}

/// <summary>
/// Persists the Composer history pane's expand/collapse state under the user's local app-data
/// directory, alongside the history itself. View state is a convenience, so every failure degrades
/// to no settings rather than surfacing.
/// </summary>
public static class ComposerViewStateStore
{
    /// <summary>
    /// Ceiling on remembered hosts. The file is written from host names that ultimately come off
    /// the wire, so it needs a bound that a long capture session cannot grow past.
    /// </summary>
    public const int MaxHosts = 500;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "composer-view.json");

    public static void Save(ComposerViewState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        path ??= DefaultPath;

        var sanitized = new ComposerViewState { CollapsedHosts = Clean(state.CollapsedHosts) };

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(sanitized));
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static ComposerViewState Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return new ComposerViewState();
            var state = JsonSerializer.Deserialize<ComposerViewState>(File.ReadAllText(path));
            return state is null
                ? new ComposerViewState()
                : new ComposerViewState { CollapsedHosts = Clean(state.CollapsedHosts) };
        }
        catch (IOException)
        {
            return new ComposerViewState();
        }
        catch (JsonException)
        {
            return new ComposerViewState();
        }
        catch (UnauthorizedAccessException)
        {
            return new ComposerViewState();
        }
    }

    /// <summary>
    /// Applies the same bounds on the way in and on the way out, so a hand-edited file cannot carry
    /// a host the pane would never itself have produced, and one bad entry does not discard the rest.
    /// </summary>
    private static List<string> Clean(List<string>? hosts) => (hosts ?? [])
        .Where(ComposerHistoryView.IsDisplayableHost)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxHosts)
        .ToList();
}
