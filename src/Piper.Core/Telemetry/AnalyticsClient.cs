using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Piper.Core.Telemetry;

/// <summary>
/// Queues sanitised events, spools them to disk, and uploads them in batches.
///
/// The spool file is the design: one append-only NDJSON file buys durability across a crash, a
/// buffer for an offline machine, and - because it is plain text the user can open - the record of
/// exactly what Piper is about to send.
///
/// Delivery moves that file aside rather than reading from it in place, so a batch in flight is
/// never disturbed by events arriving behind it; the two files are the whole storage design.
/// </summary>
public sealed class AnalyticsClient : IDisposable
{
    /// <summary>
    /// Where reports are sent. A compile-time constant rather than a setting on purpose: an endpoint
    /// read from a configuration file would be an exfiltration target the moment that file could be
    /// influenced by anything Piper captures.
    /// </summary>
    public const string DefaultEndpoint = "https://analyticsnew.overwolf.com";

    /// <summary>Identifies Piper in the shared collector, reported as app_type on every event.</summary>
    public const string AppType = "piper";

    /// <summary>
    /// Conservative ceiling for the whole request line. The closed vocabulary keeps real events far
    /// below it; anything that somehow exceeds it is dropped rather than truncated, because a
    /// truncated URL is a silently wrong event rather than a missing one.
    /// </summary>
    private const int MaxUrlLength = 1800;

    /// <summary>In-memory events awaiting their trip to the spool. Oldest are dropped past this.</summary>
    private const int MaxQueued = 500;

    /// <summary>
    /// Ceiling for the pending spool, reached only when the endpoint has been unreachable for a long
    /// time. During delivery a claimed batch sits in a second file, so the bound on total analytics
    /// disk use is twice this, not once.
    /// </summary>
    private const long MaxSpoolBytes = 1024 * 1024;

    /// <summary>
    /// Ceiling for a claimed batch. A drain can overshoot <see cref="MaxSpoolBytes"/> by one queue's
    /// worth, so this leaves headroom above that; beyond it the file did not come from Piper.
    /// </summary>
    private const long MaxInflightBytes = 2 * MaxSpoolBytes;

    /// <summary>Longest spooled line worth parsing. A real event is a few hundred bytes.</summary>
    private const int MaxSpoolLineLength = 4096;

    /// <summary>Events delivered per flush, and therefore requests made per flush.</summary>
    private const int MaxBatchEvents = 200;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<AnalyticsEvent> _queue = new();
    private readonly AnalyticsSettings _settings;
    private readonly Uri _endpoint;
    private readonly string _appVersion;
    private readonly string _spoolPath;
    private readonly string _inflightPath;
    private readonly string? _settingsPath;
    private readonly Func<string?>? _machineId;
    private readonly Action? _forgetMachineId;
    private readonly string _runId = Guid.NewGuid().ToString("n");
    private readonly Lock _spoolLock = new();

    /// <summary>
    /// Serialises every change to <see cref="_settings"/> and its write to disk. The UI thread
    /// changes the choice while the pump thread mints identifiers, and two overlapping
    /// <c>File.WriteAllText</c> calls mean one throws and is swallowed - losing, in the worst case,
    /// the write that recorded an opt-out, so collection silently resumes on the next launch.
    /// </summary>
    private readonly Lock _settingsLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http;

    private int _queued;
    private int _consecutiveFailures;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private Task? _pump;
    private bool _disposed;

    public AnalyticsClient(
        AnalyticsSettings settings,
        string appVersion,
        Uri? endpoint = null,
        string? spoolPath = null,
        string? settingsPath = null,
        Func<string?>? machineId = null,
        Action? forgetMachineId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(appVersion);

        _settings = settings;
        _appVersion = appVersion;
        _endpoint = endpoint ?? new Uri(DefaultEndpoint);
        _spoolPath = spoolPath ?? Path.Combine(AnalyticsSettingsStore.DefaultSpoolDirectory, "pending.jsonl");
        // A batch left here by a run that died mid-upload is picked up and retried on the next start.
        _inflightPath = _spoolPath + ".sending";
        _settingsPath = settingsPath;
        // A callback, not a value: resolving it allocates a durable identifier, and that must
        // not happen until a report is actually about to go out - which is past both consent
        // gates. It also keeps the platform-specific store out of this assembly.
        _machineId = machineId;
        _forgetMachineId = forgetMachineId;

        // Loopback is allowed so the delivery path can be tested without standing up a certificate;
        // everything else must be HTTPS, because these reports cross the public internet.
        if (!_endpoint.IsLoopback && _endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Analytics endpoint must use HTTPS.", nameof(endpoint));
        }

        // Piper is frequently the machine's system proxy. Routing its own reports through itself
        // would loop them back into the capture and, while stopped, fail every upload.
        _http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Piper/{_appVersion}");
    }

    /// <summary>Identifies the current run. Events sharing it form one session in the reports.</summary>
    public string RunId => _runId;

    public string SpoolPath => _spoolPath;

    /// <summary>The live settings this client reads. Change them through the methods below.</summary>
    public AnalyticsSettings Settings => _settings;

    /// <summary>
    /// Applies the user's choice and persists it. Opting out also forgets the installation
    /// identifier and discards anything still spooled: a user who turns this off has not agreed to
    /// the delivery of what was collected before they found the switch.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_settingsLock)
        {
            _settings.Enabled = enabled;
            if (!enabled)
            {
                _settings.InstallId = null;

                // The machine identifier is the one that actually reaches the collector, so
                // forgetting only the installation identifier would leave the user re-linkable to
                // everything they reported before - precisely what the dialog says will not happen.
                _forgetMachineId?.Invoke();
                DiscardSpool();
            }

            AnalyticsSettingsStore.Save(_settings, _settingsPath);
        }
    }

    /// <summary>Records that the user has been told what is collected. Until this runs, nothing uploads.</summary>
    public void RecordNoticeShown(string version)
    {
        lock (_settingsLock)
        {
            _settings.NoticeShownVersion = version;
            AnalyticsSettingsStore.Save(_settings, _settingsPath);
        }
    }

    private void DiscardSpool()
    {
        _queue.Clear();
        Interlocked.Exchange(ref _queued, 0);

        try
        {
            lock (_spoolLock)
            {
                if (File.Exists(_spoolPath)) File.Delete(_spoolPath);
                if (File.Exists(_inflightPath)) File.Delete(_inflightPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts the background flush loop. Safe to call once; later calls do nothing.</summary>
    public void Start()
    {
        if (_pump is not null || _disposed) return;
        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    /// <summary>
    /// Records an event. Returns without touching disk or the network, so it is safe to call from
    /// the UI thread; the background loop does the work.
    /// </summary>
    public void Track(string name, params (string Key, string Value)[] properties)
    {
        if (_disposed || !_settings.Enabled) return;

        var recorded = AnalyticsSchema.Create(name, properties, DateTimeOffset.UtcNow, _runId);
        if (recorded is null) return;

        _queue.Enqueue(recorded);
        if (Interlocked.Increment(ref _queued) > MaxQueued && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <summary>
    /// Writes everything queued to the spool synchronously. Called from the crash handler before the
    /// modal dialog, because the report that matters most is the one from the run that did not
    /// survive to the next timer tick.
    /// </summary>
    public void FlushToDisk()
    {
        if (_disposed) return;

        try
        {
            lock (_spoolLock)
            {
                DrainToSpool();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Moves queued events to the spool and, if the user has seen the notice and left analytics on,
    /// tries to deliver what is there. Exposed for the smoke tests, which drive it directly rather
    /// than waiting out the timer.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        FlushToDisk();

        // Two independent gates, both of which must be open: the user asked for this, and they were
        // actually asked. Neither alone is enough to justify a request leaving the machine.
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.NoticeShownVersion)) return;
        if (DateTimeOffset.UtcNow < _nextAttempt) return;

        AnalyticsEvent[] batch;
        lock (_spoolLock)
        {
            batch = TakeBatch();
        }

        if (batch.Length == 0) return;

        // One request per event: the collector counts events, it does not accept batches. Sent in
        // order and stopped at the first failure, so a dropped connection halfway through costs a
        // retry of the remainder rather than a re-send of everything.
        // Enabled is re-read before every event, not just when the batch was claimed: a user who
        // opts out while a flush is in flight has the rest of the batch stopped mid-way, which is
        // what the dialog promises. Without this, a claimed batch finishes regardless.
        var delivered = 0;
        while (delivered < batch.Length
            && _settings.Enabled
            && await TryDeliverAsync(batch[delivered], cancellationToken).ConfigureAwait(false))
        {
            delivered++;
        }

        if (delivered == batch.Length)
        {
            _consecutiveFailures = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            lock (_spoolLock)
            {
                DiscardInflight();
            }
        }
        else
        {
            // Drop what did get through, so a retry does not count those events twice.
            if (delivered > 0)
            {
                lock (_spoolLock)
                {
                    // An opt-out during the request already deleted the spool; re-creating it here
                    // would resurrect those events and ship them if reporting is ever switched on
                    // again. Honour the newer decision.
                    if (_settings.Enabled) RewriteInflight(batch.Skip(delivered));
                    else DiscardInflight();
                }
            }

            // Back off rather than retry a dead endpoint every minute for the life of the process.
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 16);
            var delay = TimeSpan.FromSeconds(Math.Min(
                FlushInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures), MaxBackoff.TotalSeconds));
            _nextAttempt = DateTimeOffset.UtcNow + delay;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Dispose stopped waiting and tore down the transport mid-request. Nothing to recover:
            // the batch is still on disk and goes out on the next run.
        }
    }

    /// <summary>Appends queued events to the spool. Caller holds <see cref="_spoolLock"/>.</summary>
    private void DrainToSpool()
    {
        if (_queue.IsEmpty) return;

        var builder = new StringBuilder();
        while (_queue.TryDequeue(out var recorded))
        {
            Interlocked.Decrement(ref _queued);
            builder.Append(JsonSerializer.Serialize(recorded)).Append('\n');
        }

        if (builder.Length == 0) return;

        var directory = Path.GetDirectoryName(_spoolPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        TrimSpoolIfOversized();
        File.AppendAllText(_spoolPath, builder.ToString());
    }

    /// <summary>
    /// Keeps the newest half of the spool when it hits the cap. Checked before appending, so the
    /// ceiling is soft - one drain's worth of events can overshoot it - which is fine for a bound
    /// whose job is to stop unbounded growth, not to hit an exact size. Rewriting the whole file is
    /// affordable because this only runs once the endpoint has been unreachable long enough to fill
    /// it.
    /// </summary>
    private void TrimSpoolIfOversized()
    {
        try
        {
            if (!File.Exists(_spoolPath) || new FileInfo(_spoolPath).Length <= MaxSpoolBytes) return;

            var lines = File.ReadAllLines(_spoolPath);
            var keep = lines.Skip(lines.Length / 2).ToArray();
            File.WriteAllText(_spoolPath, keep.Length == 0 ? string.Empty : string.Join('\n', keep) + "\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Claims the spooled events for delivery by moving the whole file aside, then parses them.
    ///
    /// The move is what makes delivery safe: the uploader owns the in-flight file outright, so
    /// events appended - or a trim performed - while the request is in the air cannot shift the
    /// batch underneath it. Removing a delivered batch by counting lines off the front of a live
    /// file was wrong for exactly that reason.
    ///
    /// Re-validating here rather than trusting the file is the second half of the sanitiser's
    /// guarantee. The spool sits in the user's profile and the UI invites them to open it, so the
    /// bytes read back are untrusted input like any other: they are parsed, passed through
    /// <see cref="AnalyticsSchema.Create"/> again, and anything that fails is dropped. That also
    /// makes a half-written line - which the crash handler can produce, since appending is not
    /// atomic - cost one event instead of poisoning every future batch.
    ///
    /// Caller holds <see cref="_spoolLock"/>.
    /// </summary>
    private AnalyticsEvent[] TakeBatch()
    {
        try
        {
            if (!File.Exists(_inflightPath))
            {
                if (!File.Exists(_spoolPath)) return [];
                File.Move(_spoolPath, _inflightPath);
            }

            // An in-flight file is always a trimmed spool, so it cannot legitimately be this large.
            // Past the ceiling it has been grown or hand-edited outside Piper, and is discarded
            // rather than parsed - the alternative is reading an arbitrary amount into memory.
            if (new FileInfo(_inflightPath).Length > MaxInflightBytes)
            {
                File.Delete(_inflightPath);
                return [];
            }

            var batch = new List<AnalyticsEvent>();

            // Enumerated lazily and capped: the batch length is the number of outbound requests one
            // flush makes, so leaving it open-ended lets the contents of a file decide how much
            // traffic Piper generates.
            foreach (var line in File.ReadLines(_inflightPath))
            {
                if (batch.Count >= MaxBatchEvents) break;
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxSpoolLineLength) continue;

                AnalyticsEvent? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<AnalyticsEvent>(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (parsed is null) continue;

                // Null-conditional because an explicit "props": null in the file deserialises to a
                // null dictionary regardless of the property initialiser. This file is user-editable
                // and the UI invites opening it, so it gets the same treatment as any hostile input:
                // without this the dereference throws past every catch here and kills the pump.
                var properties = parsed.Properties?
                    .Take(AnalyticsSchema.MaxProperties)
                    .Select(property => (property.Key, property.Value))
                    .ToArray();

                var revalidated = AnalyticsSchema.Create(
                    parsed.Name,
                    properties,
                    parsed.Timestamp,
                    AnalyticsSchema.SanitiseValue(parsed.RunId));
                if (revalidated is not null) batch.Add(revalidated);
            }

            if (batch.Count == 0)
            {
                // Nothing survived, so there is nothing to send and nothing worth keeping. The file
                // must still go: the move above only happens when no in-flight file exists, so
                // leaving an empty one here would block delivery on this run and every run after it.
                File.Delete(_inflightPath);
                return [];
            }

            return batch.ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Drops the delivered batch. Only the in-flight file is touched, so events that arrived during
    /// the request are untouched in the live spool. Caller holds <see cref="_spoolLock"/>.
    /// </summary>
    private void DiscardInflight()
    {
        try
        {
            if (File.Exists(_inflightPath)) File.Delete(_inflightPath);
        }
        catch (IOException)
        {
            // Left in place, so the batch is re-sent next tick rather than lost. The collector
            // discards duplicates; losing the file here would lose the events outright.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Replaces the in-flight file with what is left to send. Caller holds the lock.</summary>
    private void RewriteInflight(IEnumerable<AnalyticsEvent> remaining)
    {
        try
        {
            var lines = remaining.Select(recorded => JsonSerializer.Serialize(recorded));
            File.WriteAllText(_inflightPath, string.Join('\n', lines) + "\n");
        }
        catch (IOException)
        {
            // The batch stays as it was, so the retry re-sends events the collector already has.
            // Duplicates are the acceptable failure here; losing the rest of the batch is not.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Sends one event as a Counter request, matching the collector the CurseForge apps already
    /// report to: the event name, the machine identifier, and the rest as a JSON <c>Extra</c> value.
    /// </summary>
    private async Task<bool> TryDeliverAsync(AnalyticsEvent recorded, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildCounterUrl(recorded);
            if (url is null) return true; // Unsendable, and retrying will not change that. Drop it.

            // Headers only: nothing here reads the body, and buffering one from a third-party
            // endpoint is an attacker-controlled amount of memory per event.
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;

            // A refusal the collector will repeat forever - a bad name, a wrong path - would
            // otherwise block every event queued behind it for the life of the install. Retrying is
            // only worth it when the failure might not recur.
            var permanent = (int)response.StatusCode is >= 400 and < 500
                and not 408 and not 429;
            return permanent;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            // Dispose tore down the transport mid-send; the batch stays on disk for the next run.
            return false;
        }
        catch (TaskCanceledException)
        {
            // Covers the client timeout as well as shutdown; either way the spool keeps the event.
            return false;
        }
    }

    /// <summary>
    /// Builds the Counter URL, or <see langword="null"/> if it would be too long to send.
    ///
    /// Everything rides in the query string, which is the collector's contract rather than a choice
    /// made here. That is survivable only because the vocabulary is closed: the values are short
    /// tokens from a fixed list, so nothing captured can end up in a URL that intermediaries log.
    /// </summary>
    private Uri? BuildCounterUrl(AnalyticsEvent recorded)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in recorded.Properties) extra[key] = value;

        // Applied last so a caller key can never shadow them. Mirrors the keys the CurseForge
        // service merges in for its own callers; Piper reports directly, so it supplies them itself.
        extra[AnalyticsProperties.AppVersion] = AnalyticsSchema.SanitiseValue(_appVersion);
        extra[AnalyticsProperties.OsVersion] = AnalyticsSchema.SanitiseValue(OperatingSystemName());
        extra[AnalyticsProperties.AppType] = AppType;

        // Without these the funnel this whole event set exists to measure cannot be computed: the
        // collector stamps arrival time, and a machine that was offline for a day would report a
        // day's worth of steps as if they happened at once, in no particular order.
        extra[AnalyticsProperties.Run] = recorded.RunId;
        extra[AnalyticsProperties.Timestamp] =
            recorded.Timestamp.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        var identity = ResolveIdentity();
        if (identity is null) return null;

        var url = $"{_endpoint.GetLeftPart(UriPartial.Authority)}/analytics/Counter"
            + $"?Name={Uri.EscapeDataString(recorded.Name)}"
            + $"&MUID={Uri.EscapeDataString(identity)}"
            + $"&Extra={Uri.EscapeDataString(JsonSerializer.Serialize(extra))}";

        // Escaped, unlike the desktop caller in the CurseForge app, whose Extra goes in raw.
        return url.Length <= MaxUrlLength && Uri.TryCreate(url, UriKind.Absolute, out var built) ? built : null;
    }

    /// <summary>A short OS name, bounded and token-shaped so it passes the schema unchanged.</summary>
    private static string OperatingSystemName()
    {
        var version = Environment.OSVersion.Version;
        return $"win_{version.Major}.{version.Build}";
    }

    /// <summary>
    /// Resolves the identifier reported with a batch: the stable machine identifier when the host
    /// supplies one, otherwise an identifier minted for this installation.
    ///
    /// Both are validated on the way out, not just on the way in. They are read back from a registry
    /// value and a settings file the user can edit, which would otherwise make them the only
    /// unbounded, unsanitised strings in the payload. Anything that is not a plain token is
    /// discarded rather than echoed.
    /// </summary>
    private string? ResolveIdentity()
    {
        lock (_settingsLock)
        {
            // Resolving mints and stores an identifier, so it must not happen once the user has
            // opted out - the machine-id callback would recreate in the registry exactly what
            // SetEnabled(false) just deleted, moments after deleting it.
            if (!_settings.Enabled) return null;

            var machine = _machineId?.Invoke();
            if (!string.IsNullOrEmpty(machine) && AnalyticsSchema.SanitiseValue(machine) == machine)
            {
                return machine;
            }

            var existing = _settings.InstallId;
            if (!string.IsNullOrEmpty(existing) && AnalyticsSchema.SanitiseValue(existing) == existing)
            {
                return existing;
            }

            var minted = Guid.NewGuid().ToString("n");
            _settings.InstallId = minted;
            AnalyticsSettingsStore.Save(_settings, _settingsPath);
            return minted;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shutdown.Cancel();
        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The pump only ever faults on cancellation; nothing to recover here.
        }

        // Deliberately disk-only on the way out: a shutdown path must not wait on the network.
        FlushToDiskDuringDispose();

        _shutdown.Dispose();
        _http.Dispose();
    }

    private void FlushToDiskDuringDispose()
    {
        try
        {
            lock (_spoolLock)
            {
                DrainToSpool();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
