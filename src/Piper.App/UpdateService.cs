using System.Security.Cryptography;
using System.Text.Json;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

namespace Piper.App;

/// <summary>
/// Checks Piper's public GitHub releases and downloads a user-approved installer.
/// Update checks use <see cref="RequestExecutor"/> so they are ordinary, visible Piper sessions.
/// </summary>
internal sealed class UpdateService(RequestExecutor executor)
{
    internal const string LatestReleaseEndpoint = "https://api.github.com/repos/tomwolfgang/piper/releases/latest";

    private const int MaxReleaseMetadataBytes = 1024 * 1024;
    private const int MaxChecksumManifestBytes = 1024 * 1024;
    private const long MaxInstallerBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken ct)
    {
        var request = new HttpRequestData
        {
            Method = "GET",
            RequestTarget = LatestReleaseEndpoint,
            Url = new Uri(LatestReleaseEndpoint),
        };
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        var session = await executor.ExecuteAsync(request, timeout.Token, isUpdateCheck: true).ConfigureAwait(false);
        return ParseLatestReleaseResponse(currentVersion, session.Response, session.Error);
    }

    internal static UpdateCheckResult ParseLatestReleaseResponse(
        Version currentVersion, HttpResponseData? response, string? requestError = null)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        if (response is null)
            return UpdateCheckResult.Failed(requestError ?? Strings.Updates.NoResponse);
        if (response.StatusCode != 200)
            return UpdateCheckResult.Failed(Strings.Updates.HttpStatus(response.StatusCode));
        if (response.Body.Length > MaxReleaseMetadataBytes)
            return UpdateCheckResult.Failed(Strings.Updates.ResponseTooLarge);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind != JsonValueKind.String)
                return UpdateCheckResult.Failed(Strings.Updates.NoReleaseTag);

            var tagName = tag.GetString();
            if (!TryParseReleaseVersion(tagName, out var releaseVersion))
                return UpdateCheckResult.Failed(Strings.Updates.UnsupportedVersion);

            if (releaseVersion.CompareTo(currentVersion) <= 0)
                return UpdateCheckResult.UpToDate;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return UpdateCheckResult.Failed(Strings.Updates.NoAssets);

            var installerName = $"Piper-{releaseVersion}-setup.exe";
            Uri? installerUri = null;
            Uri? checksumUri = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                if (!asset.TryGetProperty("name", out var nameElement)
                    || !asset.TryGetProperty("browser_download_url", out var urlElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || urlElement.ValueKind != JsonValueKind.String)
                    continue;

                var name = nameElement.GetString();
                if (!TryGetGitHubDownloadUri(urlElement.GetString(), out var uri)) continue;
                if (string.Equals(name, installerName, StringComparison.Ordinal)) installerUri = uri;
                if (string.Equals(name, "SHA256SUMS.txt", StringComparison.Ordinal)) checksumUri = uri;
            }

            if (installerUri is null || checksumUri is null)
                return UpdateCheckResult.Failed(Strings.Updates.MissingInstallerOrManifest);

            return UpdateCheckResult.Available(new UpdateRelease(releaseVersion, installerName, installerUri, checksumUri));
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(Strings.Updates.InvalidMetadata);
        }
    }

    public async Task<UpdateDownloadResult> DownloadAndVerifyInstallerAsync(UpdateRelease release, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);

        var updateDirectory = Path.Combine(Path.GetTempPath(), "Piper", "updates", Guid.NewGuid().ToString("N"));
        var installerPath = Path.Combine(updateDirectory, release.InstallerName);
        var downloaded = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);
            Directory.CreateDirectory(updateDirectory);
            using var client = CreateDownloadClient();
            var expectedHash = await DownloadExpectedHashAsync(client, release, timeout.Token).ConfigureAwait(false);
            if (expectedHash is null)
                return UpdateDownloadResult.Failed(Strings.Updates.ManifestMissingInstaller);

            await DownloadFileAsync(client, release.InstallerUri, installerPath, timeout.Token).ConfigureAwait(false);
            await using var installer = File.OpenRead(installerPath);
            var actualHash = await SHA256.HashDataAsync(installer, timeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                return UpdateDownloadResult.Failed(Strings.Updates.ChecksumMismatch);

            downloaded = true;
            return UpdateDownloadResult.Downloaded(installerPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UpdateDownloadResult.Failed(Strings.Updates.DownloadCancelled);
        }
        catch (OperationCanceledException)
        {
            return UpdateDownloadResult.Failed(Strings.Updates.DownloadTimedOut);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return UpdateDownloadResult.Failed(Strings.Updates.DownloadFailed(ex.Message));
        }
        finally
        {
            if (!downloaded) TryDeleteDirectory(updateDirectory);
        }
    }

    private static HttpClient CreateDownloadClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static async Task<byte[]?> DownloadExpectedHashAsync(HttpClient client, UpdateRelease release, CancellationToken ct)
    {
        var manifest = await DownloadBytesAsync(client, release.ChecksumUri, MaxChecksumManifestBytes, ct).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(manifest);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !string.Equals(parts[1].TrimStart('*'), release.InstallerName, StringComparison.Ordinal)) continue;
            if (parts[0].Length != 64) continue;
            try
            {
                var hash = Convert.FromHexString(parts[0]);
                if (hash.Length == 32) return hash;
            }
            catch (FormatException) { }
        }
        return null;
    }

    private static async Task DownloadFileAsync(HttpClient client, Uri uri, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxInstallerBytes)
            throw new IOException(Strings.Updates.InstallerTooLarge);

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            written += read;
            if (written > MaxInstallerBytes) throw new IOException(Strings.Updates.InstallerTooLarge);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> DownloadBytesAsync(HttpClient client, Uri uri, int maximumBytes, CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maximumBytes)
            throw new IOException(Strings.Updates.ManifestTooLarge);

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new IOException(Strings.Updates.ManifestTooLarge);
            output.Write(buffer, 0, read);
        }
    }

    private static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var numericPart = tag.Trim().TrimStart('v', 'V');
        var prereleaseOrBuild = numericPart.IndexOfAny(['-', '+']);
        if (prereleaseOrBuild >= 0) numericPart = numericPart[..prereleaseOrBuild];
        if (!Version.TryParse(numericPart, out var parsed) || parsed is null
            || parsed.Build < 0 || parsed.Revision >= 0)
            return false;

        version = parsed;
        return true;
    }

    private static bool TryGetGitHubDownloadUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps
            && string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate!;
            return true;
        }

        uri = null!;
        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record UpdateRelease(Version Version, string InstallerName, Uri InstallerUri, Uri ChecksumUri);

internal sealed record UpdateCheckResult(UpdateRelease? Release, string? Error)
{
    public static UpdateCheckResult UpToDate { get; } = new(null, null);
    public bool IsUpdateAvailable => Release is not null;
    public static UpdateCheckResult Available(UpdateRelease release) => new(release, null);
    public static UpdateCheckResult Failed(string error) => new(null, error);
}

internal sealed record UpdateDownloadResult(string? InstallerPath, string? Error)
{
    public bool IsDownloaded => InstallerPath is not null;
    public static UpdateDownloadResult Downloaded(string path) => new(path, null);
    public static UpdateDownloadResult Failed(string error) => new(null, error);
}
