using System.Text;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// Compose and replay requests, with a live search over everything already sent from here.
/// </summary>
/// <remarks>
/// The history pane makes finding part of composing: type a query, see matching requests you have
/// sent, load one, edit, send. It uses the same <see cref="SearchQuery"/> grammar as the
/// session-list filter, so a query learned in one place works in the other. Sending shows the
/// response in this panel too, so reading it never means going back to the capture grid.
/// </remarks>
public sealed class ComposerPanel : UserControl
{
    private readonly SessionStore _store;
    private readonly RequestExecutor _executor;

    // History pane
    private readonly ComposerHistoryTree _historyTree;

    // Editor pane
    private readonly ComboBox _method;
    private readonly TextBox _url;
    private readonly TextBox _headers;
    private readonly TextBox _body;
    private readonly TextBox _rawEditor;
    private readonly TabControl _editorTabs;
    /// <summary>Index of the Raw page in <see cref="_editorTabs"/>; see where the pages are added.</summary>
    private const int RawTabIndex = 1;
    private readonly Button _execute;
    private readonly Label _status;
    // The same inspector the capture grid uses, so a composed response is read with exactly the
    // tooling - pretty-printed JSON, hex, image preview - that a captured one gets.
    private readonly MessageInspector _response = new("Response", showImageViewer: true) { Dock = DockStyle.Fill };
    // Persisted Composer history belongs in this panel, not in SessionStore. The latter drives
    // the capture list, so restoring history there made an old composed request appear as the
    // first "captured" session every time Piper started.
    private readonly List<Session> _history = [];

    private CancellationTokenSource? _inFlight;

    public ComposerPanel(SessionStore store, RequestExecutor executor)
    {
        _store = store;
        _executor = executor;

        _history.AddRange(ComposerHistoryStore.Load());

        // --------------------------------------------------------- history pane

        _historyTree = new ComposerHistoryTree();
        _historyTree.SessionActivated += (_, session) => LoadSession(session);
        _historyTree.RemoveRequested += (_, sessions) => RemoveFromHistory(sessions);

        // ---------------------------------------------------------- editor pane

        _method = new ComboBox
        {
            Width = 90,
            DropDownStyle = ComboBoxStyle.DropDown,
            Font = Palette.Mono,
        };
        _method.Items.AddRange(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"]);
        _method.SelectedIndex = 0;
        _method.TextChanged += (_, _) => UpdateBodyWarning();

        _url = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = Palette.Mono,
            PlaceholderText = "https://example.com/api/v1/resource",
        };
        _url.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _ = ExecuteAsync();
        };

        _execute = new Button { Width = 90, Dock = DockStyle.Right, Text = "Send" };
        _execute.Click += (_, _) => _ = ExecuteAsync();

        var urlRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
        urlRow.Controls.Add(_url);
        urlRow.Controls.Add(_execute);

        var methodRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
        _method.Dock = DockStyle.Left;
        methodRow.Controls.Add(urlRow);
        methodRow.Controls.Add(_method);

        _headers = MakeEditor("Host: example.com\r\nAccept: application/json");
        _body = MakeEditor(string.Empty);
        _body.TextChanged += (_, _) => UpdateBodyWarning();
        _rawEditor = MakeEditor(string.Empty);

        _editorTabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        _editorTabs.TabPages.Add(NewPage("Headers", _headers));
        _editorTabs.TabPages.Add(NewPage("Raw", _rawEditor)); // == RawTabIndex
        _editorTabs.Selecting += OnEditorTabSelecting;
        _editorTabs.Deselecting += OnEditorTabDeselecting;

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            ForeColor = Palette.TextDim,
            Font = Palette.Mono,
            Padding = new Padding(4, 3, 0, 0),
        };

        var bodyHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "  Body",
            ForeColor = Palette.Text,
            Font = Palette.UiFontBold,
            Padding = new Padding(0, 4, 0, 0),
        };
        var bodyPane = new Panel { Dock = DockStyle.Fill };
        bodyPane.Controls.Add(_body);
        bodyPane.Controls.Add(bodyHeader);

        var editorSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
        };
        editorSplit.Panel1.Controls.Add(_editorTabs);
        editorSplit.Panel2.Controls.Add(bodyPane);

        var requestPane = new Panel { Dock = DockStyle.Fill };
        requestPane.Controls.Add(editorSplit);
        requestPane.Controls.Add(methodRow);

        _responseSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
            Panel1MinSize = 120,
            Panel2MinSize = 80,
        };
        _responseSplit.Panel1.Controls.Add(requestPane);
        _responseSplit.Panel2.Controls.Add(_response);

        var editorPane = new Panel { Dock = DockStyle.Fill };
        editorPane.Controls.Add(_responseSplit);
        editorPane.Controls.Add(_status);

        // ------------------------------------------------------------- assembly

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
        };
        split.Panel1MinSize = 260;
        split.Panel1.Controls.Add(_historyTree);
        split.Panel2.Controls.Add(editorPane);
        Controls.Add(split);

        _split = split;

        _store.SessionAdded += (_, _) => _searchDirty = true;
        _store.SessionUpdated += (_, _) => _searchDirty = true;
        _store.Cleared += (_, _) => _searchDirty = true;

        _searchTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _searchTimer.Tick += (_, _) =>
        {
            if (!_searchDirty) return;
            _searchDirty = false;
            RunSearch();
        };
        _searchTimer.Start();

        // Accept a session dropped anywhere on the Composer, not just on the tab above it, the way
        // the AutoResponder panel already does. The tab strip alone is a 20px target, and it is
        // the only one reachable while a different tab is showing, so the two paths complement
        // each other rather than replace each other.
        EnableSessionDrop(this);

        _historyTree.SetHistory(_history);
        UpdateBodyWarning();
    }

    /// <summary>
    /// Registers every control on this panel as a session drop target, mirroring
    /// <c>MainForm.EnableSazFileDrop</c>.
    /// </summary>
    /// <remarks>
    /// Recursive rather than a list of the big controls, because a drop lands on the deepest
    /// control under the cursor and that recursion has already set <see cref="Control.AllowDrop"/>
    /// on all of them. Any control left out would therefore not fall through to this panel -- it
    /// would swallow the drop and do nothing, which is worse than not accepting it at all.
    ///
    /// A control that already claims drops is left to whoever claimed it. At this point in
    /// construction that is only the response inspector's image and video targets, which load a
    /// dropped session's response as media; running both handlers would make one drop do two
    /// unrelated things. Their children are still visited, because the inspector's tab strip claims
    /// itself without claiming the pages inside it, and those pages would otherwise stay dead.
    /// </remarks>
    private void EnableSessionDrop(Control control)
    {
        if (!control.AllowDrop)
        {
            control.AllowDrop = true;
            control.DragEnter += OnSessionDragOver;
            control.DragOver += OnSessionDragOver;
            control.DragDrop += OnSessionDrop;
        }

        foreach (Control child in control.Controls) EnableSessionDrop(child);
    }

    private static void OnSessionDragOver(object? sender, DragEventArgs e)
    {
        if (DraggedSession(e) is not null) e.Effect = DragDropEffects.Copy;
    }

    private void OnSessionDrop(object? sender, DragEventArgs e)
    {
        if (DraggedSession(e) is { } session) LoadSession(session);
    }

    /// <summary>The grid drags the <see cref="Session"/> object itself, not a serialised form of it.</summary>
    /// <remarks>
    /// A request with no URL at all cannot seed the editor, so it is refused outright rather than
    /// silently loading a blank one.
    /// </remarks>
    private static Session? DraggedSession(DragEventArgs e) =>
        e.Data?.GetData(typeof(Session)) is Session { Request.Url: not null } session ? session : null;

    private readonly SplitContainer _split;
    private readonly SplitContainer _responseSplit;
    private bool _responseSplitPositioned;
    private readonly System.Windows.Forms.Timer _searchTimer;
    private volatile bool _searchDirty;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Set the splitter once the control has a real width; doing it in the constructor
        // clamps against the design-time size.
        if (_split.Width > 400) _split.SplitterDistance = Math.Min(380, _split.Width / 3);
    }

    /// <summary>
    /// Gives the request editor rather more room than the response once the pane has a real
    /// height. As with the outer splitter, doing this in the constructor is clamped against the
    /// design-time size.
    /// </summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_responseSplitPositioned || _responseSplit.Height <= 300) return;
        _responseSplit.SplitterDistance = _responseSplit.Height * 3 / 5;
        _responseSplitPositioned = true;
    }

    private static TextBox MakeEditor(string initial) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = Palette.Mono,
        BorderStyle = BorderStyle.None,
        AcceptsTab = true,
        Text = initial,
    };

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    // ----------------------------------------------------------------- history

    private void RunSearch() => _historyTree.Rebuild();

    /// <summary>Appends sessions (e.g. a Fiddler "request-only" archive import) to the persisted
    /// Composer history, alongside whatever is already there. Each one is reduced to exactly what
    /// a reload from disk would produce - a composed, request-only entry - so an imported row does
    /// not read as a failed Composer send before the next restart and then change afterwards.</summary>
    public void AppendToHistory(IEnumerable<Session> sessions)
    {
        var added = sessions
            .Where(session => session.Request is not null)
            .Select(session => new Session
            {
                Request = session.Request,
                IsComposed = true,
                Completed = session.Completed ?? session.Started,
            })
            .ToArray();
        if (added.Length == 0) return;

        _history.AddRange(added);
        // Keep memory and the file in step: Save persists only the newest MaxEntries, so a large
        // archive must not leave thousands of extra rows visible until the next restart.
        if (_history.Count > ComposerHistoryStore.MaxEntries)
            _history.RemoveRange(0, _history.Count - ComposerHistoryStore.MaxEntries);
        ComposerHistoryStore.Save(_history);
        _searchDirty = true;
    }

    /// <summary>Drops the given sends from the persisted history. The tree hands over every send
    /// behind the selected rows, so removing a repeated request removes all of its sends rather
    /// than leaving a row that still looks the same with a smaller count.</summary>
    private void RemoveFromHistory(IReadOnlyList<Session> sessions)
    {
        var doomed = sessions.ToHashSet();
        if (doomed.Count == 0 || _history.RemoveAll(doomed.Contains) == 0) return;

        ComposerHistoryStore.Save(_history);
        RunSearch();
    }

    // ------------------------------------------------------------------ editor

    /// <summary>Fills the editor from a captured session. Also used by the grid's context menu.</summary>
    public void LoadSession(Session session)
    {
        if (session.Request is not { } request) return;

        _method.Text = request.Method;
        _url.Text = session.Url;

        var headerText = new StringBuilder();
        foreach (var header in request.Headers)
        {
            // Content-Length is recalculated at send time; keeping a stale one is a footgun.
            // Host is always re-derived from the URL box at send time too (see
            // RequestExecutor.PrepareHeaders) -- showing a stale one here would be misleading.
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            headerText.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }
        _headers.Text = headerText.ToString();

        _body.Text = request.Body.Length > 0 && ContentCodec.LooksTextual(request.ContentType, request.DecodedBody)
            ? request.BodyAsText()
            : string.Empty;

        _rawEditor.Text = BuildRawText();
        // History is persisted as raw request text only, so a loaded entry has no response of its
        // own. Leaving the previous send's body on screen beside it would read as this request's.
        ShowResponse(null, "Response   (not sent yet)");
        _status.Text = $"Loaded #{session.Id} - edit and press Send (or Enter in the URL box).";
        // Raw shows the request line, headers and body at once, which is what you want when
        // reviewing something already sent -- and it is what every caller here loads a session for.
        _editorTabs.SelectedIndex = RawTabIndex;
        _url.Focus();
    }

    /// <summary>Keeps the Raw tab in sync when it is opened from the structured tabs.</summary>
    private void OnEditorTabSelecting(object? sender, TabControlCancelEventArgs e)
    {
        if (e.TabPageIndex == RawTabIndex) _rawEditor.Text = BuildRawText();
    }

    /// <summary>Parses the Raw tab back into the structured fields when leaving it.</summary>
    private void OnEditorTabDeselecting(object? sender, TabControlCancelEventArgs e)
    {
        if (e.TabPageIndex != RawTabIndex) return;
        if (!RequestExecutor.TryParseRaw(_rawEditor.Text, out var parsed, out _)) return;

        _method.Text = parsed.Method;
        _url.Text = parsed.Url?.ToString() ?? parsed.RequestTarget;

        var headerText = new StringBuilder();
        foreach (var header in parsed.Headers)
            headerText.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        _headers.Text = headerText.ToString();
        _body.Text = parsed.Body.Length > 0 ? Encoding.UTF8.GetString(parsed.Body) : string.Empty;
    }

    private string BuildRawText() =>
        RequestExecutor.BuildRawText(_method.Text, _url.Text, _headers.Text, _body.Text);

    private bool TryBuildRequest(out HttpRequestData request, out string error)
    {
        request = new HttpRequestData
        {
            Method = _method.Text.Trim().ToUpperInvariant(),
            HttpVersion = "HTTP/1.1",
            Headers = HeaderCollection.Parse(_headers.Text),
        };

        error = string.Empty;
        var url = _url.Text.Trim();
        if (url.Length == 0)
        {
            error = "Enter a URL.";
            return false;
        }

        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = $"'{url}' is not a valid absolute URL.";
            return false;
        }

        request.Url = parsed;
        request.RequestTarget = parsed.PathAndQuery;

        var bodyText = _body.Text;
        request.Body = bodyText.Length > 0 ? Encoding.UTF8.GetBytes(bodyText) : [];
        return true;
    }

    /// <summary>PUT/POST usually shouldn't go out with an empty body -- flag it before the user
    /// finds out the hard way from the far side.</summary>
    private void UpdateBodyWarning()
    {
        var method = _method.Text.Trim().ToUpperInvariant();
        var likelyMistake = method is "POST" or "PUT" && _body.Text.Length == 0;
        _body.BackColor = likelyMistake ? Palette.SurfaceWarning : Palette.Surface;
    }

    private async Task ExecuteAsync()
    {
        if (_inFlight is not null)
        {
            await _inFlight.CancelAsync();
            return;
        }

        if (!TryBuildRequest(out var template, out var error))
        {
            _status.Text = error;
            _status.ForeColor = Palette.StatusServerError;
            return;
        }

        await ExecuteRequestAsync(template, fromEditor: true);
    }

    /// <summary>Replays a captured request without changing the Composer editor fields.</summary>
    public Task ResendAsync(Session session)
    {
        if (session.Request is null) return Task.CompletedTask;

        var replay = session.Request.Clone();
        // The Composer deliberately writes an HTTP/1.1 request over its upstream TCP
        // connection. Reusing an HTTP/2 or HTTP/3 capture's request-line version here makes
        // an h1 origin correctly reject it with 505 HTTP Version Not Supported.
        replay.HttpVersion = "HTTP/1.1";
        return ExecuteRequestAsync(replay, fromEditor: false);
    }

    /// <param name="fromEditor">
    /// Whether this send came from the Composer's own editor. A Ctrl+R replay does not, so it must
    /// leave both the history and the response pane alone: a body shown beneath a request that did
    /// not produce it reads as that request's, which is the same trap <see cref="LoadSession"/>
    /// clears the pane to avoid.
    /// </param>
    private async Task ExecuteRequestAsync(HttpRequestData template, bool fromEditor)
    {
        void Surface(HttpResponseData? response, string summary)
        {
            if (fromEditor) ShowResponse(response, summary);
        }

        if (_inFlight is not null)
        {
            await _inFlight.CancelAsync();
            return;
        }

        _inFlight = new CancellationTokenSource();
        _execute.Text = "Cancel";
        _status.ForeColor = Palette.TextDim;
        _status.Text = "Sending...";

        try
        {
            var session = await _executor.ExecuteAsync(template, _inFlight.Token);
            if (fromEditor)
            {
                // The explicit Composer Send action owns this history. Ctrl+R replays are
                // captured in the session list, but intentionally do not become history.
                _history.Add(session);
                ComposerHistoryStore.Save(_history);
                _searchDirty = true;
            }

            if (session.State == SessionState.Failed)
            {
                _status.Text = $"Failed: {session.Error}{CertificateFailureHint.For(session.Error)}";
                _status.ForeColor = Palette.StatusServerError;
                Surface(null, $"Response   FAILED - {session.Error}{CertificateFailureHint.For(session.Error)}");
            }
            else
            {
                _status.ForeColor = Palette.ForStatus(session.StatusCode, false, false, false);
                _status.Text = $"#{session.Id}  {session.StatusCode}  {session.Duration.TotalMilliseconds:N0} ms  {Format.Size(session.ResponseSize)}";
                Surface(session.Response,
                    $"Response   {session.Response?.StartLine}   {session.Duration.TotalMilliseconds:N0} ms   {Format.Size(session.ResponseSize)}");
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
            Surface(null, "Response   (cancelled)");
        }
        catch (Exception ex)
        {
            _status.Text = $"Error: {ex.Message}";
            _status.ForeColor = Palette.StatusServerError;
            Surface(null, $"Response   ERROR - {ex.Message}");
        }
        finally
        {
            _inFlight?.Dispose();
            _inFlight = null;
            _execute.Text = "Send";
        }
    }

    /// <summary>
    /// Points the response inspector at what came back, so reading it never means leaving the
    /// Composer for the capture grid. Always called on every send outcome, including the failing
    /// ones, so a stale body can never sit beside a request that did not produce it.
    /// </summary>
    private void ShowResponse(HttpResponseData? response, string summary)
    {
        _response.SetMessage(response, summary);
        if (response is not null) _response.SelectBestTab();
    }

    /// <summary>Puts focus in the search box; used by the Ctrl+K shortcut.</summary>
    public void FocusSearch() => _historyTree.FocusSearch();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchTimer.Dispose();
            _inFlight?.Dispose();
        }
        base.Dispose(disposing);
    }
}
