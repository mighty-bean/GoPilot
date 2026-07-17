namespace GoPilot;

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Globalization;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

public partial class MainForm : Form
{
    [DllImport("user32.dll")] private static extern bool SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
    private const int WM_SETREDRAW = 0x000B;

    /// <summary>
    /// Suspends visual redraws on <paramref name="rtb"/>, runs <paramref name="action"/>,
    /// then resumes and invalidates — preventing the scroll-up/scroll-down flicker caused
    /// by retroactive Select+insert sequences.
    /// </summary>
    private static void WithoutRedraw(RichTextBox rtb, Action action)
    {
        SendMessage(rtb.Handle, WM_SETREDRAW, false, 0);
        try { action(); }
        finally
        {
            SendMessage(rtb.Handle, WM_SETREDRAW, true, 0);
            rtb.Invalidate();
        }
    }
    private readonly CopilotService _copilot = new();
    private readonly LocalFilterService _localFilter = new();
    private readonly PromptHistory _promptHistory = new();
    private readonly List<string> _attachments = new();
    private readonly HashSet<string> _streamingSessions = new();
    // Structured output blocks feeding the WebView2 Rendered tab
    private readonly List<OutputBlock> _outputBlocks = new();
    private bool _webViewReady = false;
    // The single file: URI that the WebView2 is allowed to navigate to.
    private string? _outputHtmlUri;
    // Rolling meta block used by AppendOutput so plain-text status/setup lines
    // appear in the Rendered tab as well as the Raw tab. Reset (closed) whenever
    // an explicit non-meta WebView block is appended or the output is cleared.
    private OutputBlock? _currentMetaBlock;
    private Color _currentMetaColor;
    // Maps toolCallId → char offset AFTER "  🔧 name  args" text (insertion point for ✓/✗)
    private readonly Dictionary<string, int> _toolStartPositions = new();
    // Maps toolCallId → char offset of the ○ character for retroactive ○→◉/✗ replacement
    private readonly Dictionary<string, int> _subAgentStartPositions = new();
    // Maps sub-agent toolCallId → display name for currently active (in-flight) sub-agents
    private readonly Dictionary<string, string> _activeSubAgents = new();
    // Live sub-agent SESSION ids reported via session.created/session.deleted
    // lifecycle events. Used as a backstop signal that a sub-agent has truly
    // ended even when its paired subagent.completed/failed event is missed.
    private readonly HashSet<string> _activeSubAgentSessions = new();
    // Watchdog that fires if the main session has been idle awaiting sub-agents
    // for an extended period without any sub-agent completion/lifecycle activity.
    // Guards against the UI getting stuck on "Working..." when a subagent.completed
    // event is dropped or never delivered.
    private readonly System.Windows.Forms.Timer _subAgentWatchdog = new() { Interval = 60_000 };
    private int _completedAgentCount = 0;
    // True when the main session went idle but sub-agents are still running (Fleet mode);
    // completion is deferred until the last sub-agent finishes.
    private bool _mainSessionIdle = false;
    private double _totalBytesReceived = 0;
    private string? _mainSessionId;
    // Tracks whether the main session is currently working. The SDK's
    // session.idle event is the authoritative "done" signal: when it fires for
    // the main session (and no live sub-agents remain), this is reset to 0.
    // Increments on each Dispatch are best-effort bookkeeping for display only;
    // never rely on the count being exact across queued sends.
    private int _pendingCount = 0;
    private bool _reconnecting = false; // true while an automatic reconnect is in progress
    private bool _autoRefreshPromptShown = false; // suppresses the 85% nag once per session
    private bool _refreshInProgress = false; // gate to prevent overlapping Compact/Restart calls
    private bool _cliUpdateChecked = false; // ensures the npm CLI check runs only once per session
    // Guards comboBoxEffort.SelectedIndexChanged from firing during programmatic
    // re-population (e.g. when the user switches model and we snap the effort
    // combo to the new model's highest tier). The handler must not push the new
    // effort to the old model via SetModelAsync.
    private bool _suspendEffortHandler = false;
    // One-shot preferred effort applied during the initial RefreshEffortCombo
    // call so the persisted LastEffort survives launch. Cleared after first
    // use so subsequent model switches revert to "highest available" behaviour.
    private string? _pendingPreferredEffort;
    // False until PopulateModelsAsync finishes during startup. Suppresses
    // SaveSessionPreferences during the initial combo population (which fires
    // SelectedIndexChanged for both model and effort) so we don't thrash
    // gopilot.ini before the user has actually made a choice.
    private bool _uiReady = false;
    // Reason a deferred handoff is pending (e.g. mode/fleet change). When non-null,
    // the next DispatchPromptAsync runs an automatic summary-and-restart before
    // forwarding the user's prompt, so context survives the option change.
    private string? _pendingHandoffReason = null;
    private const int AutoRefreshThresholdPercent = 85;
    private GoPilotSettings _settings = new();
    private readonly SessionMetadataStore _sessionStore = new();

    // Phase 4.5 - Collapsible sections in the Rendered tab.
    // One open Reasoning section and one open Tool section may be active per
    // sessionId at a time. The next event of a different kind (or session.idle)
    // closes them with a smart summary. Keyed by SessionId so main and sub-agent
    // sessions never bleed into each other's groups.
    private readonly Dictionary<string, OpenReasoningSection> _openReasoningSections = new();
    private readonly Dictionary<string, OpenToolSection> _openToolSections = new();
    // Maps tool-call id -> tracking metadata for ToolProgress / ToolComplete routing.
    // Lines stay registered here even after completion until the parent section closes,
    // so smart-summary categorisation has access to all the line metadata.
    private readonly Dictionary<string, OpenToolLine> _openToolLines = new();
    private int _nextSectionId = 0;
    // Active "Thinking..." pill id for the current main-session turn, if any.
    // Set immediately after Send; cleared on the first session message of the turn.
    private string? _activeThinkingId;
    private int _nextThinkingId = 0;

    private bool bIsFreeTierAutoOnly = false;

    private readonly string? _startupFolder;

    public MainForm() : this(null) { }

    public MainForm(string? startupFolder)
    {
        _startupFolder = startupFolder;
        InitializeComponent();
        InitializeOptionIcons();
        WireUpEvents();

        // Load persisted settings and sync with service
        _settings = GoPilotSettings.Load();
        _copilot.SkillTreeFolders = _settings.SkillTreeFolders;
        _copilot.McpServers = _settings.McpServers;
        _copilot.McpDisabledDiscovered = _settings.McpDisabledDiscovered;
        _copilot.CavemanMode = _settings.CavemanMode;
        menuSessionCaveman.Checked = _settings.CavemanMode;
        menuSessionShowSteps.Checked = _settings.DetailsDefaultOpen;
        // Local LLM filter: restore toggle/config and bring the model online in
        // the background so the first send is not blocked on detection/pull.
        _localFilter.Endpoint  = _settings.LocalFilterEndpoint;
        _localFilter.Model     = _settings.LocalFilterModel;
        _localFilter.Threshold = _settings.LocalFilterThreshold;
        _localFilter.Enabled   = _settings.LocalFilterEnabled;
        menuOptionLocalFilter.Checked = _settings.LocalFilterEnabled;
        // Restore the last-used Auto-approve / Fleet toggles before the mode
        // combo is populated so ApplyModeChangeAsync's "Autopilot implies
        // auto-approve" rule sees the correct starting state. The CheckedChanged
        // handlers fire but are no-ops at this point: AutoApprove just syncs
        // _copilot.AutoApprove, and Fleet's ScheduleHandoff guard requires a
        // live connection. Both are persisted again once _uiReady flips on.
        menuOptionAutoApprove.Checked = _settings.LastAutoApprove;
        menuOptionFleet.Checked = _settings.LastFleet;
        // Tool Search defaults to on (matching the runtime default). Restore the
        // persisted toggle and threshold before the service syncs below.
        menuOptionToolSearch.Checked = _settings.ToolSearchEnabled;
        // Stash the persisted effort for one-shot consumption by the first
        // RefreshEffortCombo call (triggered from PopulateModelsAsync via the
        // model combo's SelectedIndexChanged handler).
        _pendingPreferredEffort = string.IsNullOrWhiteSpace(_settings.LastEffort)
            ? null
            : _settings.LastEffort;
        UpdateSkillTreeTooltip();
        UpdateSkillSourcesTooltip();

        // Populate mode combo from the SDK enum and select the first entry
        PopulateModeCombo();
        // Sync service with the UI defaults set above
        _copilot.AutoApprove = menuOptionAutoApprove.Checked;
        _copilot.FleetMode = menuOptionFleet.Checked;
        _copilot.ToolSearchEnabled = menuOptionToolSearch.Checked;
        _copilot.ToolSearchDeferThreshold = _settings.ToolSearchDeferThreshold;

        // A3: seed the meter with a visible "starting state" so the affordance
        // is discoverable before the first AssistantUsageEvent arrives.
        OnContextUsageChanged(new ContextUsageEventArgs());

        // Apply the dark renderer to ALL ToolStrips (MenuStrip, DropDowns,
        // StatusStrip) via the global manager so dropdowns inherit it too.
        ToolStripManager.Renderer = new DarkMenuRenderer();

        // Auto-scaling doubles the status strip's margins and padding at 200% DPI,
        // making it unnecessarily tall. Collapse them in Load (after PerformAutoScale
        // has run) so the strip auto-sizes to the text height with minimal padding.
        Load += (_, _) =>
        {
            statusStrip.SizingGrip = false;
            statusStrip.Padding = Padding.Empty;
            foreach (ToolStripItem item in statusStrip.Items)
                item.Margin = new Padding(item.Margin.Left, 1, item.Margin.Right, 1);
        };
    }

    // ── Event wiring ─────────────────────────────────────────────────────────

    private void WireUpEvents()
    {
        buttonSend.Click += async (_, _) => await SendPromptAsync();
        buttonStop.Click += async (_, _) => await StopAsync();
        menuReferencesAddFile.Click += ButtonAddFile_Click;
        menuReferencesAddFolder.Click += ButtonAddFolder_Click;
        menuSessionNew.Click += async (_, _) => await OpenFolderAndConnectAsync();
        buttonHistoryPrev.Click += (_, _) => NavigateHistoryBack();
        buttonHistoryNext.Click += (_, _) => NavigateHistoryForward();
        richTextBoxPrompt.KeyDown += RichTextBoxPrompt_KeyDown;
        richTextBoxPrompt.FilesDropped += (_, paths) =>
        {
            foreach (var path in paths)
                AddAttachment(path);
        };
        richTextBoxPrompt.UnrecognizedFormatDropped += (_, formats) =>
        {
            // The drop was accepted on the strength of a recognised format
            // (e.g. CF_VSSTGPROJECTITEMS) but no file paths could be extracted.
            // Surface the format list so this drag source can be supported.
            string formatList = formats.Count > 0
                ? string.Join(", ", formats)
                : "(none reported)";
            AppendOutput(
                "[drag-drop] Could not extract file paths from this drag source. " +
                "Formats present: " + formatList + "\r\n",
                AppTheme.ColorMeta);
        };
        AttachPromptContextMenu();

        this.Shown += async (_, _) =>
        {
            await InitializeWebViewAsync();
            await CheckForUpdatesAsync();
            await PopulateModelsAsync();

            if (!string.IsNullOrEmpty(_startupFolder))
                await ConnectToFolderAsync(_startupFolder);
        };

        menuHelpShow.Click += (_, _) => ShowHelpAsync();
        menuHelpAbout.Click += (_, _) =>
        {
            using var dlg = new AboutDialog();
            dlg.ShowDialog(this);
        };
        menuToolsPowershell.Click += (_, _) => OpenPowershell();
        menuSessionSummarize.Click += async (_, _) => await SendQuickCommandAsync(
            "Please provide a concise summary of what we've discussed and accomplished so far in this session.");
        menuSessionClear.Click += (_, _) => ClearActiveOutput();
        menuSessionRefreshCompact.Click += async (_, _) => await RunCompactAsync();
        menuSessionRefreshRestart.Click += async (_, _) => await RunRestartWithSummaryAsync();
        menuSessionRefreshFresh.Click += async (_, _) => await RunFreshStartAsync();
        menuSessionPast.Click += async (_, _) => await BrowsePastSessionsAsync();
        menuSessionCaveman.CheckedChanged += async (_, _) =>
        {
            var enabled = menuSessionCaveman.Checked;
            _copilot.CavemanMode = enabled;
            _settings.CavemanMode = enabled;
            try { _settings.Save(); } catch { /* best-effort persist */ }

            // If a session is active, fire a one-shot side instruction so the
            // change takes effect immediately. We deliberately do NOT trigger a
            // ScheduleHandoff: the directive is also baked into BuildSystemMessage,
            // so any future session/restart already inherits the new style.
            if (_copilot.IsConnected && _mainSessionId != null)
            {
                var instruction = enabled
                    ? "CAVEMAN MODE ON. From now on: caveman speak. Fewest tokens. Nouns and verbs. " +
                      "No grammar filler (the, is, are, of). Short words. Be blunt. " +
                      "Skip openers, closures, preambles, filler transitions. " +
                      "Apply this to ALL prose output, including your reasoning/thinking output, " +
                      "not just the final response. " +
                      "Preserve code, paths, command syntax, and tool output verbatim."
                    : "CAVEMAN MODE OFF. From now on: respond normally with proper grammar, " +
                      "complete sentences, and your usual level of explanation.";
                try { await DispatchPromptAsync(instruction); }
                catch { /* best-effort; surface nothing to the user */ }
            }
        };
        menuSessionShowSteps.CheckedChanged += (_, _) =>
        {
            _settings.DetailsDefaultOpen = menuSessionShowSteps.Checked;
            try { _settings.Save(); } catch { /* best-effort persist */ }
            // Affects newly-emitted sections only; existing closed/open sections
            // are not retroactively reopened or recollapsed.
        };
        menuOptionLocalFilter.CheckedChanged += async (_, _) =>
        {
            var enabled = menuOptionLocalFilter.Checked;
            _localFilter.Enabled = enabled;
            _settings.LocalFilterEnabled = enabled;
            try { _settings.Save(); } catch { /* best-effort persist */ }
            if (enabled)
                await InitLocalFilterAsync();
        };
        menuToolsLocalSettings.Click += async (_, _) => await EditLocalFilterSettingsAsync();
        menuToolsMcpServers.Click += (_, _) => EditMcpServers();
        menuToolsExplorer.Click += (_, _) => OpenExplorer();
        menuToolsVSCode.Click += (_, _) => OpenVSCode();
        menuToolsSkillTree.Click += (_, _) => EditSkillTree();
        menuToolsSkillSources.Click += (_, _) => EditSkillSources();
        menuToolsBrowseCatalog.Click += (_, _) => BrowseSkillCatalog();
        menuToolsPermissions.Click += (_, _) => ShowWorkspacePermissions();
        menuReferencesListAgents.Click += (_, _) => ShowAgentList();
        menuReferencesListSkills.Click += (_, _) => ShowSkillList();
        menuReferencesListPrompts.Click += (_, _) => ShowPromptList();

        buttonOptions.Click += (_, _) =>
            contextMenuOptions.Show(buttonOptions, new Point(0, buttonOptions.Height));

        menuOptionAutoApprove.CheckedChanged += (_, _) =>
        {
            _copilot.AutoApprove = menuOptionAutoApprove.Checked;
            SaveSessionPreferences();
            if (_copilot.IsConnected)
                EmitPermissionsStatus("Auto-approve toggled");
        };

        menuOptionFleet.CheckedChanged += (_, _) =>
        {
            _copilot.FleetMode = menuOptionFleet.Checked;
            SaveSessionPreferences();
            if (_copilot.IsConnected && _mainSessionId != null)
            {
                var state = menuOptionFleet.Checked ? "enabled" : "disabled";
                ScheduleHandoff($"Fleet {state}");
            }
        };

        menuOptionToolSearch.CheckedChanged += (_, _) =>
        {
            // Tool search is a session-creation setting (ToolSearchConfig is read
            // when a session is created/resumed), so a mid-session change only
            // takes effect after a new session -- schedule the same summary-and-
            // restart handoff used by Fleet and Mode changes.
            _copilot.ToolSearchEnabled = menuOptionToolSearch.Checked;
            _settings.ToolSearchEnabled = menuOptionToolSearch.Checked;
            try { _settings.Save(); } catch { /* best-effort persist */ }
            if (_copilot.IsConnected && _mainSessionId != null)
            {
                var state = menuOptionToolSearch.Checked ? "enabled" : "disabled";
                ScheduleHandoff($"Tool search {state}");
            }
        };

        comboBoxModel.SelectedIndexChanged += async (_, _) =>
        {
            var model = comboBoxModel.SelectedItem?.ToString() ?? "gpt-4.1";
            // Snap the Effort dropdown to the new model's supported set BEFORE
            // calling UpdateModelAsync so the runtime model-switch carries the
            // correct effort in a single round trip.
            RefreshEffortCombo(model);
            var effort = comboBoxEffort.SelectedItem?.ToString();
            SaveSessionPreferences();
            try { await _copilot.UpdateModelAsync(model, effort); }
            catch { /* ignore */ }
        };

        comboBoxEffort.SelectedIndexChanged += async (_, _) =>
        {
            if (_suspendEffortHandler) return;
            if (!comboBoxEffort.Enabled) return;
            var effort = comboBoxEffort.SelectedItem?.ToString();
            SaveSessionPreferences();
            try { await _copilot.UpdateReasoningEffortAsync(effort); }
            catch { /* ignore */ }
        };

        comboBoxMode.SelectedIndexChanged += async (_, _) =>
        {
            SaveSessionPreferences();
            await ApplyModeChangeAsync();
        };

        _copilot.ConnectionStateChanged += (_, state) =>
            InvokeOnUI(() => UpdateConnectionStatus(state));

        _copilot.SessionCreated += (_, args) =>
            InvokeOnUI(() => OnSessionCreated(args.SessionId, args.IsSubAgent));

        _copilot.MessageReceived += (_, args) =>
            InvokeOnUI(() => AppendMessage(args));

        _copilot.SessionIdleForSession += (_, sessionId) =>
            InvokeOnUI(() => OnSessionIdle(sessionId));

        _copilot.SubAgentSessionEnded += (_, sessionId) =>
            InvokeOnUI(() => OnSubAgentSessionEnded(sessionId));

        _subAgentWatchdog.Tick += (_, _) =>
        {
            _subAgentWatchdog.Stop();
            if (_mainSessionIdle)
                ForceCompleteStaleSubAgents("watchdog timeout");
        };

        _copilot.PermissionRequested += Copilot_PermissionRequested;
        _copilot.UserInputRequested += Copilot_UserInputRequested;

        _copilot.ContextUsageChanged += (_, args) =>
            InvokeOnUI(() => OnContextUsageChanged(args));

        // AIC usage updates from the Copilot SDK/CLI (authoritative when present)
        _copilot.AicUsageChanged += (_, args) =>
            InvokeOnUI(() => OnAicUsageChanged(args));
    }

    private void InvokeOnUI(Action action)
    {
        if (IsHandleCreated && InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    // ── WebView2 initialization ──────────────────────────────────────────────

    private void OnAicUsageChanged(AicUsageEventArgs args)
    {
        if (args == null || args.AicUsed == null || args.AicUsed.Value <= 0)
        {
            toolStripStatusLabelAic.Text = "";
            toolStripStatusLabelAic.ToolTipText = "";
            return;
        }

        // Cumulative AI Credits spent this session. Show two-decimal precision and
        // expose the raw nano-AIU breakdown via the tooltip.
        var s = args.AicUsed.Value.ToString("0.##", CultureInfo.InvariantCulture);
        toolStripStatusLabelAic.Text = $"AIC: {s}";
        toolStripStatusLabelAic.ToolTipText = string.IsNullOrEmpty(args.Display)
            ? $"{args.AicUsed.Value:0.##########} AI Credits used this session"
            : args.Display;
    }

    /// <summary>
    /// Opens the Local LLM Settings dialog so the user can point the filter at
    /// an Ollama server (this machine or another on the network by host/IP),
    /// pick the model, and set the confidence threshold. Persists the changes
    /// and, when the filter is on and the endpoint or model changed, brings it
    /// back online against the new target.
    /// </summary>
    private async Task EditLocalFilterSettingsAsync()
    {
        using var dlg = new LocalLlmSettingsDialog(
            _settings.LocalFilterEndpoint,
            _settings.LocalFilterModel,
            _settings.LocalFilterThreshold);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        var endpointChanged = !string.Equals(_settings.LocalFilterEndpoint, dlg.Endpoint, StringComparison.OrdinalIgnoreCase);
        var modelChanged    = !string.Equals(_settings.LocalFilterModel,    dlg.Model,    StringComparison.OrdinalIgnoreCase);

        _settings.LocalFilterEndpoint  = dlg.Endpoint;
        _settings.LocalFilterModel     = dlg.Model;
        _settings.LocalFilterThreshold = dlg.Threshold;
        _localFilter.Endpoint  = dlg.Endpoint;
        _localFilter.Model     = dlg.Model;
        _localFilter.Threshold = dlg.Threshold;
        try { _settings.Save(); } catch { /* best-effort persist */ }

        // Re-detect against the new target only when the filter is on and the
        // connection actually changed; a threshold-only tweak takes effect on
        // the next prompt without a round-trip.
        if (_localFilter.Enabled && (endpointChanged || modelChanged))
            await InitLocalFilterAsync();
    }

    /// <summary>
    /// Brings the local LLM filter online (VRAM detection, model selection,
    /// Ollama reachability + model presence) on a background thread, then
    /// reports the outcome in the output panel. Best-effort: failures leave the
    /// toggle on but the filter inert, so prompts still reach the cloud.
    /// </summary>
    private async Task InitLocalFilterAsync()
    {
        try
        {
            await Task.Run(() => _localFilter.InitializeAsync());
            // Persist the auto-selected model so the next launch reuses it.
            _settings.LocalFilterModel = _localFilter.Model;
            try { _settings.Save(); } catch { /* best-effort persist */ }
            var color = _localFilter.Available ? AppTheme.ColorMeta : AppTheme.ColorError;
            AppendOutput($"\U0001f9e0 Local filter: {_localFilter.StatusReason}\r\n\r\n", color);
        }
        catch (Exception ex)
        {
            AppendOutput($"\U0001f9e0 Local filter init failed: {ex.Message}\r\n\r\n", AppTheme.ColorError);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = _copilot.WorkspaceDataPath != null
                ? Path.Combine(_copilot.WorkspaceDataPath, "webview2")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GoPilot", "webview2-default");
            Directory.CreateDirectory(userDataFolder);

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateAsync(null, userDataFolder);
            await webViewOutput.EnsureCoreWebView2Async(env);

            // ── Lock down the WebView2 so it behaves as a pure display
            // surface, not a general-purpose browser. Disable navigation
            // affordances the user could accidentally trigger.
            var settings = webViewOutput.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;

            // The Rendered tab posts JSON messages (e.g. file-path link
            // clicks) back to us via window.chrome.webview.postMessage.
            // Wire the receiver before navigation so no early message is
            // dropped on the floor.
            webViewOutput.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

            // Intercept top-level navigations (clicked anchors, document.location
            // assignments) and popup window requests (target="_blank", window.open).
            // Any web URL is routed to the system default browser so the
            // Rendered tab never replaces the transcript with a remote page.
            webViewOutput.CoreWebView2.NavigationStarting += WebView_NavigationStarting;
            webViewOutput.CoreWebView2.FrameNavigationStarting += WebView_FrameNavigationStarting;
            webViewOutput.CoreWebView2.NewWindowRequested += WebView_NewWindowRequested;
            webViewOutput.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;

            // Navigate to the bundled output.html
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "web", "output.html");
            if (File.Exists(htmlPath))
            {
                _outputHtmlUri = new Uri(htmlPath).AbsoluteUri;
                webViewOutput.CoreWebView2.Navigate(_outputHtmlUri);
                _webViewReady = true;
            }
            else
            {
                // Fall back to Raw tab if assets are missing
                tabControlOutput.SelectedTab = tabPageRaw;
            }
        }
        catch
        {
            // WebView2 runtime not available — fall back to Raw tab
            tabControlOutput.SelectedTab = tabPageRaw;
            tabControlOutput.TabPages.Remove(tabPageRendered);
        }
    }

    /// <summary>
    /// Handles JSON messages posted by <c>output.js</c> (currently the
    /// file-path link click/right-click bridge). Unknown message shapes
    /// are ignored silently so the renderer can introduce new message
    /// types without crashing older builds.
    /// </summary>
    private void WebView_WebMessageReceived(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch
        {
            // Non-string payloads are ignored.
            return;
        }
        if (string.IsNullOrWhiteSpace(json)) return;

        string? type = null;
        string? path = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (doc.RootElement.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String)
                type = typeEl.GetString();
            if (doc.RootElement.TryGetProperty("path", out var pathEl)
                && pathEl.ValueKind == JsonValueKind.String)
                path = pathEl.GetString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(path)) return;

        switch (type)
        {
            case "openPath": OpenFilePathFromLink(path); break;
            case "revealPath": RevealFilePathFromLink(path); break;
        }
    }

    /// <summary>
    /// Cancels any navigation that would replace the bundled
    /// <c>output.html</c> document with another page, then hands web URLs
    /// (http/https/mailto) off to the system default browser via
    /// <see cref="LaunchInDefaultBrowser"/>.
    ///
    /// Only the exact <c>file:</c> URI for our bundled output.html is
    /// allowed through (needed for the initial load and recovery
    /// re-navigation). The <c>about:</c> scheme is allowed for
    /// internal WebView2 runtime operations. Everything else is
    /// cancelled, including the custom <c>kp-path:</c> scheme and
    /// any stray <c>file:</c> links the LLM may have generated.
    /// </summary>
    private void WebView_NavigationStarting(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;

        // Allow only our exact output.html file URI
        if (_outputHtmlUri != null
            && e.Uri.Equals(_outputHtmlUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        LaunchInDefaultBrowser(e.Uri);
    }

    /// <summary>
    /// Blocks all iframe/frame navigations. The Rendered tab should
    /// never load external content inside embedded frames.
    /// </summary>
    private void WebView_FrameNavigationStarting(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;

        // Allow about:blank (used by some internal frame init)
        if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
    }

    /// <summary>
    /// Safety net: if a navigation completes and we are NOT on our
    /// expected output.html page (e.g. the user managed to trigger
    /// Back/Forward before settings were locked down, or some edge
    /// case slipped through), re-navigate to output.html and replay
    /// all accumulated output blocks.
    /// </summary>
    private void WebView_NavigationCompleted(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_outputHtmlUri == null || !_webViewReady) return;

        var currentUri = webViewOutput.CoreWebView2.Source;
        if (currentUri != null
            && currentUri.Equals(_outputHtmlUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // We are on an unexpected page -- recover by re-navigating
        // to output.html. The next NavigationCompleted will hit the
        // early-return above, then we replay all blocks.
        webViewOutput.CoreWebView2.Navigate(_outputHtmlUri);
        // Use a one-shot handler to replay content after re-navigation
        webViewOutput.CoreWebView2.NavigationCompleted += ReplayAfterRecovery;
    }

    /// <summary>
    /// One-shot handler that replays all accumulated output blocks
    /// into the WebView after a recovery re-navigation to output.html.
    /// </summary>
    private void ReplayAfterRecovery(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        // Unsubscribe immediately -- this is a one-shot
        webViewOutput.CoreWebView2.NavigationCompleted -= ReplayAfterRecovery;

        if (!e.IsSuccess) return;

        foreach (var block in _outputBlocks)
        {
            WebViewAppendBlockInternal(block);
            if (block.IsComplete)
            {
                var js = $"finalizeBlock({JsString(block.Id)})";
                _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
            }
        }
    }

    /// <summary>
    /// Handles target="_blank" anchors and <c>window.open</c> calls.
    /// Always sets <c>Handled = true</c> so the WebView2 runtime does
    /// not spawn a new popup window, then routes web URLs through the
    /// system default browser.
    /// </summary>
    private void WebView_NewWindowRequested(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        LaunchInDefaultBrowser(e.Uri);
    }

    /// <summary>
    /// Shell-executes <paramref name="url"/> with the OS default handler,
    /// restricted to a small allowlist of safe schemes (http, https,
    /// mailto). Any other scheme is silently ignored so that custom
    /// schemes used internally (e.g. kp-path:) cannot be smuggled into
    /// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
    /// </summary>
    private static void LaunchInDefaultBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
         && !url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
         && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort: a missing or misconfigured default browser
            // should not crash the app or log noisily.
        }
    }

    /// <summary>
    /// Shell-executes <paramref name="path"/> with the OS default handler.
    /// Existence is re-validated here even though the transformer only
    /// emits links for files that resolved at render time, because the
    /// file may have been moved or deleted before the user clicked.
    /// </summary>
    private void OpenFilePathFromLink(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n\n{path}\n\n{ex.Message}",
                "Open File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Opens Windows Explorer with <paramref name="path"/> pre-selected.
    /// Falls back to opening the parent folder if /select fails (which
    /// can happen for paths that contain unusual characters).
    /// </summary>
    private void RevealFilePathFromLink(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch
        {
            try
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    System.Diagnostics.Process.Start("explorer.exe", parent);
            }
            catch
            {
                // Swallow; the user will see no Explorer window but no crash.
            }
        }
    }

    // ── Tab control dark theme drawing ───────────────────────────────────────

    private void TabControlOutput_DrawItem(object? sender, DrawItemEventArgs e)
    {
        var tabCtrl = (TabControl)sender!;
        var page = tabCtrl.TabPages[e.Index];
        var bounds = tabCtrl.GetTabRect(e.Index);

        bool isSelected = tabCtrl.SelectedIndex == e.Index;
        var bgColor = isSelected ? AppTheme.OutputBox : AppTheme.Surface;
        var fgColor = isSelected ? AppTheme.TextPrimary : AppTheme.TextMuted;

        using var bgBrush = new SolidBrush(bgColor);
        using var fgBrush = new SolidBrush(fgColor);

        // Inflate generously to overpaint the bright visual-styles border
        // that the system renderer draws around each tab header. Extend
        // further downward so the white seam between the tab and the
        // content area is also covered for both selected and unselected tabs.
        var fillRect = bounds;
        fillRect.Inflate(3, 3);
        fillRect.Y -= 2;   // extend upward to cover the top-edge border
        fillRect.Height += 6;   // extend downward to cover the tab-to-content seam
        e.Graphics!.SetClip(fillRect);
        e.Graphics.FillRectangle(bgBrush, fillRect);
        e.Graphics.ResetClip();

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        e.Graphics.DrawString(page.Text, tabCtrl.Font, fgBrush, bounds, sf);
    }

    /// <summary>
    /// Captures the current Model / Mode / Effort / Fleet / AutoApprove choices
    /// from the toolbar and Options dropdown into <see cref="_settings"/> and
    /// persists gopilot.ini. No-op until <see cref="_uiReady"/> flips to true
    /// at the end of <see cref="PopulateModelsAsync"/>, so the SelectedIndexChanged
    /// and CheckedChanged events that fire during initial population do not
    /// thrash the file or overwrite the persisted choice before it is applied.
    /// All writes are best-effort; failures are swallowed.
    /// </summary>
    private void SaveSessionPreferences()
    {
        if (!_uiReady) return;

        _settings.LastModel = comboBoxModel.SelectedItem?.ToString() ?? string.Empty;
        _settings.LastMode = comboBoxMode.SelectedItem?.ToString() ?? string.Empty;
        _settings.LastEffort = comboBoxEffort.Enabled
            ? (comboBoxEffort.SelectedItem?.ToString() ?? string.Empty)
            : string.Empty;
        _settings.LastFleet = menuOptionFleet.Checked;
        _settings.LastAutoApprove = menuOptionAutoApprove.Checked;

        try { _settings.Save(); } catch { /* best-effort persist */ }
    }

    /// <summary>
    /// Populates comboBoxEffort from the active service's per-model supported
    /// reasoning-effort list. The dropdown is disabled and cleared when the
    /// model does not advertise reasoning-effort support; otherwise the levels
    /// are listed highest-first and the highest level is pre-selected (unless
    /// <see cref="_pendingPreferredEffort"/> matches one of the levels, in
    /// which case that one is selected -- used to restore the last-used effort
    /// at launch).
    /// </summary>
    private void RefreshEffortCombo(string? modelId)
    {
        var efforts = _copilot.GetSupportedReasoningEfforts(modelId);

        _suspendEffortHandler = true;
        comboBoxEffort.BeginUpdate();
        try
        {
            comboBoxEffort.Items.Clear();
            if (efforts.Count == 0)
            {
                comboBoxEffort.Enabled = false;
                // Persisted effort doesn't apply to this model; discard the
                // one-shot so a subsequent model with reasoning support starts
                // from the highest-available default instead.
                _pendingPreferredEffort = null;
                return;
            }

            // Always rank highest-first so the topmost item is the default and
            // unfamiliar SDK additions sort below the documented tiers.
            var ranked = efforts
                .OrderByDescending(CopilotService.RankReasoningEffort)
                .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var e in ranked)
                comboBoxEffort.Items.Add(e);

            comboBoxEffort.Enabled = true;

            var preferredIdx = -1;
            if (!string.IsNullOrWhiteSpace(_pendingPreferredEffort))
            {
                for (var i = 0; i < ranked.Count; i++)
                {
                    if (ranked[i].Equals(_pendingPreferredEffort, StringComparison.OrdinalIgnoreCase))
                    {
                        preferredIdx = i;
                        break;
                    }
                }
                // Consume the one-shot regardless of whether it matched, so
                // later user-driven model switches behave normally (highest
                // available).
                _pendingPreferredEffort = null;
            }

            comboBoxEffort.SelectedIndex = preferredIdx >= 0 ? preferredIdx : 0; // highest available otherwise
        }
        finally
        {
            comboBoxEffort.EndUpdate();
            _suspendEffortHandler = false;
        }
    }

    // ── Combo population ──────────────────────────────────────────────────────

    // Maps each SDK mode value to the display name used throughout the app.
    // SessionMode is a readonly struct in SDK 1.0+, not an enum.
    private static readonly Dictionary<SessionMode, string> _modeDisplayNames =
        new()
        {
            [SessionMode.Interactive] = "Standard",
            [SessionMode.Plan] = "Plan",
            [SessionMode.Autopilot] = "Autopilot",
        };

    // Ordered list of all known SessionMode values (replaces Enum.GetValues since
    // SessionMode is no longer a plain enum as of SDK 1.0.0-beta.1).
    private static readonly SessionMode[] _allModes =
    [
        SessionMode.Interactive,
        SessionMode.Plan,
        SessionMode.Autopilot,
    ];

    /// <summary>
    /// Populates comboBoxMode from the known SessionMode values,
    /// using display names that match the rest of the app (Interactive -> "Standard").
    /// Pre-selects the last-used mode from settings when present and valid;
    /// otherwise selects the first entry (Standard).
    /// </summary>
    private void PopulateModeCombo()
    {
        comboBoxMode.Items.Clear();
        foreach (var mode in _allModes)
        {
            var display = _modeDisplayNames.TryGetValue(mode, out var name) ? name : mode.ToString();
            comboBoxMode.Items.Add(display);
        }

        var preferred = _settings.LastMode;
        var preferredIdx = string.IsNullOrWhiteSpace(preferred)
            ? -1
            : comboBoxMode.Items.Cast<string>()
                .Select((m, i) => (m, i))
                .Where(x => x.m.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                .Select(x => (int?)x.i)
                .FirstOrDefault() ?? -1;

        comboBoxMode.SelectedIndex = preferredIdx >= 0 ? preferredIdx : 0;
    }

    /// <summary>
    /// Queries the Copilot SDK for available models and populates comboBoxModel.
    /// Selects the last-used model from settings when present and still
    /// advertised by the SDK; otherwise picks the highest-available Claude Opus
    /// model, falling back to the highest-available Claude Sonnet model if no
    /// Opus model is present. Falls back to the service's current ActiveModel
    /// if the SDK returns nothing.
    /// 
    /// Additionally detects the special-case free-tier where the SDK advertises
    /// only the single "auto" model. In that case hide the Effort dropdown,
    /// prompt UI, and context-size meter, and display a "Free tier" message in
    /// the status area so the UI does not appear broken.
    /// </summary>
    private async Task PopulateModelsAsync()
    {
        var ids = await _copilot.ListModelsAsync();

        // If the SDK returned nothing (e.g. not yet authenticated), seed the
        // combo with the model the service is already configured to use so the
        // dropdown is never left empty.
        if (ids.Count == 0)
        {
            if (comboBoxModel.Items.Count == 0)
            {
                // Prefer the last-model saved in gopilot.ini when the SDK returns nothing
                // (e.g. not authenticated yet). Fall back to the service's ActiveModel.
                var fallback = !string.IsNullOrWhiteSpace(_settings.LastModel)
                    ? _settings.LastModel
                    : _copilot.ActiveModel;
                comboBoxModel.Items.Add(fallback);
                comboBoxModel.SelectedIndex = 0;
            }

            // Trigger a background model-list refresh so limits can arrive after startup.
            _ = Task.Run(async () =>
            {
                try
                {
                    var list = await _copilot.ListModelsAsync();
                    if (list.Count > 0)
                    {
                        // Invoke back to UI thread to repopulate the combo if models arrived
                        InvokeOnUI(async () => await PopulateModelsAsync());
                    }
                }
                catch { }
            });

            _uiReady = true;
            return;
        }

        comboBoxModel.BeginUpdate();
        try
        {
            comboBoxModel.Items.Clear();
            foreach (var id in ids)
                comboBoxModel.Items.Add(id);
        }
        finally
        {
            comboBoxModel.EndUpdate();
        }

        var items = comboBoxModel.Items.Cast<string>()
            .Select((m, i) => (model: m, idx: i))
            .ToList();

        var preferred = _settings.LastModel;
        var preferredIdx = string.IsNullOrWhiteSpace(preferred)
            ? (int?)null
            : items.Where(x => x.model.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                .Select(x => (int?)x.idx)
                .FirstOrDefault();

        var opusIdx = items
            .Where(x => x.model.StartsWith("claude-opus", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.model)
            .Select(x => (int?)x.idx)
            .FirstOrDefault();

        var defaultIdx = preferredIdx ?? opusIdx ?? items
            .Where(x => x.model.StartsWith("claude-sonnet", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.model)
            .Select(x => (int?)x.idx)
            .FirstOrDefault() ?? 0;

        comboBoxModel.SelectedIndex = defaultIdx;

        // If the SDK only advertised the single "auto" model, treat this as the
        // free-tier case: hide effort and prompt UI and show a "Free tier"
        // message in the status area so the UI doesn't look broken.
        bIsFreeTierAutoOnly = ids.Count == 1 && string.Equals(ids[0], "auto", StringComparison.OrdinalIgnoreCase);
        if (bIsFreeTierAutoOnly)
        {
            // Hide reasoning-effort controls
            labelEffort.Visible = false;
            comboBoxEffort.Visible = false;

            // Replace context-meter with an explanatory message
            toolStripProgressBarContext.Visible = false;
            toolStripStatusLabelContext.Text = "Free tier";
            toolStripStatusLabelContext.ToolTipText = "Using free Copilot tier — UI features limited";
        }
        else
        {
            // Restore the normal UI layout for non-free-tier accounts
            labelEffort.Visible = true;
            comboBoxEffort.Visible = true;

            toolStripProgressBarContext.Visible = true;
            toolStripStatusLabelContext.Text = string.Empty;
            toolStripStatusLabelContext.ToolTipText = string.Empty;
        }

        // The model selection above triggered RefreshEffortCombo which consumed
        // _pendingPreferredEffort. Open the gate so future user-driven changes
        // are persisted.
        _uiReady = true;
        SaveSessionPreferences();
    }



    private async Task SendPromptAsync(bool recordHistory = true)
    {
        var prompt = richTextBoxPrompt.Text.Trim();
        var pastedImages = ExtractEmbeddedImagesToTemp(richTextBoxPrompt.Rtf);

        if (string.IsNullOrEmpty(prompt) && pastedImages.Count == 0) return;

        // Ctrl+Enter bypasses buttonSend.Enabled, so re-check the workspace
        // here. Sending without a workspace would create a session whose ID
        // falls back to the literal "GoPilot" prefix and whose
        // workspaceFolder is permanently blank in gopilot-sessions.json.
        if (string.IsNullOrEmpty(_copilot.WorkingDirectory))
        {
            MessageBox.Show(this,
                "Open a workspace folder before sending a prompt.\r\n\r\n" +
                "Use Session > New Session... to pick a folder, or Session > " +
                "Past Sessions... to resume one.",
                "No Workspace Open",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Sending while a turn is in flight always interrupts the current turn
        // rather than queueing behind it. Without this guard the SDK silently
        // queues the new prompt at the CLI layer, which the user does not want.
        await InterruptActiveTurnAsync();

        if (recordHistory)
        {
            _promptHistory.Add(prompt);
            UpdateHistoryButtons();
        }
        richTextBoxPrompt.Clear();

        // Local LLM filter: answer simple requests outright (skipping the cloud)
        // or rewrite the prompt to the fewest tokens before forwarding. Bypassed
        // when off/unavailable, so the cloud always receives the original prompt.
        string? localMeta = null;
        if (_localFilter.Enabled && _localFilter.Available && !string.IsNullOrEmpty(prompt))
        {
            var r = await _localFilter.ProcessAsync(prompt, CollectLocalFilterFiles(prompt));
            if (r.Mode == LocalFilterMode.Answered)
            {
                EchoLocalAnswer(prompt, r);
                return;
            }
            if (r.Mode == LocalFilterMode.Minimized && !string.IsNullOrWhiteSpace(r.Prompt))
            {
                prompt = r.Prompt;
                var saved = r.OriginalChars - r.FinalChars;
                var why = string.IsNullOrEmpty(r.Note) ? "forwarding to cloud" : $"cloud needed - {r.Note}";
                localMeta = saved > 0
                    ? $"\U0001f9e0 Local ({r.ModelLabel}): minimized {r.OriginalChars} -> {r.FinalChars} chars (saved {saved}), {why}"
                    : $"\U0001f9e0 Local ({r.ModelLabel}): {why}";
            }
        }

        (int OriginalChars, int CavemanChars)? cavemanStats = null;
        if (menuSessionCaveman.Checked && !string.IsNullOrEmpty(prompt))
        {
            var (caveman, originalChars, cavemanChars) =
                CavemanTransformer.TransformWithStats(prompt);
            prompt = caveman;
            if (originalChars > 0)
                cavemanStats = (originalChars, cavemanChars);
        }

        if (localMeta != null)
            AppendOutput(localMeta + "\r\n\r\n", AppTheme.ColorMeta);

        await DispatchPromptAsync(prompt, pastedImages, cavemanStats);
    }

    /// <summary>
    /// Gathers small text files for the local filter so it can answer file-based
    /// requests offline: explicit attachments plus any @relative tokens in the
    /// prompt, resolved under the workspace. Skips binary/oversized files and
    /// caps the total fed so codellama's context is not blown.
    /// </summary>
    private List<(string Name, string Content)> CollectLocalFilterFiles(string prompt)
    {
        const int PerFileCap = 30 * 1024;
        const int TotalCap   = 60 * 1024;
        var files = new List<(string, string)>();
        var root = _copilot.WorkingDirectory;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        var candidates = new List<string>(_attachments);

        // @-prefixed relative paths: @src/foo.cs
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(prompt, @"@([^\s]+)"))
        {
            var rel = m.Groups[1].Value;
            candidates.Add(!string.IsNullOrEmpty(root) ? Path.Combine(root, rel) : rel);
        }

        // Bare absolute paths typed directly into the prompt, e.g.:
        //   C:\dev\project\Foo.cs  or  /home/user/project/Foo.cs
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(prompt,
                     @"(?:[A-Za-z]:\\|/)(?:[^\s,;'""()\r\n]+[/\\])*[^\s,;'""()\r\n]+\.\w+"))
        {
            candidates.Add(m.Value);
        }

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path) || !seen.Add(path)) continue;
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > PerFileCap) continue;
                var bytes = File.ReadAllBytes(path);
                if (Array.IndexOf(bytes, (byte)0) >= 0) continue; // skip binary
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                if (total + text.Length > TotalCap) continue;
                total += text.Length;
                files.Add((Path.GetFileName(path), text));
            }
            catch { /* unreadable file; skip */ }
        }
        return files;
    }

    /// <summary>
    /// Renders a locally-resolved answer (cloud skipped) into both output tabs:
    /// the user echo, a meta line noting model + confidence, and the answer as a
    /// Markdown assistant block. No tokens are spent on the cloud LLM.
    /// </summary>
    private void EchoLocalAnswer(string prompt, LocalFilterResult r)
    {
        if (_mainSessionId != null)
            SetSessionDescriptionIfEmpty(_mainSessionId, prompt);

        AppendRaw($"\U0001f464 You: {prompt}\r\n\r\n", AppTheme.ColorUser);
        var userBlock = new OutputBlock(BlockKind.User)
        {
            Label = "\U0001f464 You:",
            Content = prompt,
            IsComplete = true
        };
        _outputBlocks.Add(userBlock);
        WebViewAppendBlock(userBlock);

        AppendOutput(
            $"\U0001f9e0 Local ({r.ModelLabel}) conf {r.Confidence:0.00}: answered locally, cloud skipped (saved ~{r.OriginalChars} prompt chars + cloud response)\r\n\r\n",
            AppTheme.ColorMeta);

        AppendRaw($"\U0001f9e0 Local: {r.Answer}\r\n\r\n", AppTheme.ColorAssistant);
        var ansBlock = new OutputBlock(BlockKind.Assistant)
        {
            Label = "\U0001f9e0 Local:",
            Content = r.Answer,
            IsComplete = true
        };
        _outputBlocks.Add(ansBlock);
        WebViewAppendBlock(ansBlock);
    }

    private async Task StopAsync()
    {
        // No-op when nothing is in flight -- clicking Stop on an idle session
        // should not paint a phantom STOP / Halted pair.
        if (_pendingCount <= 0) return;

        // Surface the stop command immediately so the user can see their click
        // was received, then abort, then mark the halt as its consequence.
        AppendOutput(
            "\r\nSTOP. Do not perform any further actions. Wait for my next instruction.\r\n",
            AppTheme.ColorError);

        await InterruptActiveTurnAsync();

        AppendOutput("[STOPPED] Assistant Halted\r\n\r\n", AppTheme.ColorError);
    }

    // Aborts any turn currently in flight and waits briefly for the SDK's
    // session.idle to drop _pendingCount back to 0. Returns immediately when
    // nothing is in flight. The 1500ms cap is a safety net so a missed idle
    // event can never wedge the UI; in practice the abort settles within a
    // single message-pump tick on local CLI sessions.
    private async Task InterruptActiveTurnAsync()
    {
        if (_pendingCount <= 0) return;

        try { await _copilot.AbortAsync(); }
        catch { /* ignore */ }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_pendingCount > 0 && sw.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(50);
        }

        // The SDK does not always emit AssistantFinal after an abort, which
        // would leave the in-flight streaming block alive in _streamingBlocks.
        // Subsequent AssistantDelta events from the next turn would then find
        // that stale block and merge the new response into the interrupted
        // one (visible as a single Assistant section spanning multiple turns
        // with no header between them). Force-finalize here so the next turn
        // always starts in a fresh block, and close any open Reasoning/Tool
        // sections for the same reason.
        FinalizeAllStreamingBlocks();
        ClearSectionTrackers();
    }

    // Closes every in-flight Assistant streaming block across all sessions.
    // Mirrors FinalizeStreamingBlock(sessionId) but operates on the whole
    // dictionary -- used by InterruptActiveTurnAsync where we cannot trust
    // the SDK to emit AssistantFinal after an abort.
    private void FinalizeAllStreamingBlocks()
    {
        if (_streamingBlocks.Count == 0) return;

        foreach (var block in _streamingBlocks.Values)
        {
            block.IsComplete = true;
            WebViewFinalizeBlock(block);
        }
        _streamingBlocks.Clear();
    }

    private void ShowHelp()
    {
        AppendOutput("❓ GoPilot — Quick Help\r\n\r\n", AppTheme.ColorUser);

        AppendOutput("── Getting started ──────────────────────────────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "1. Choose Session > New Session... to connect Copilot to a project directory.\r\n" +
            "2. Type a prompt and press Send (or Ctrl+Enter).\r\n" +
            "3. Attach files or folders via the References menu (or right-click the prompt).\r\n" +
            "4. Use ▲ / ▼ on the left edge of the prompt to navigate history.\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── Toolbar controls ─────────────────────────────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "  Model        – Choose the AI model (GPT-4.1, Claude Sonnet/Opus, …)\r\n" +
            "  Mode         – Standard | Plan (plan before acting) | Autopilot (fully autonomous)\r\n" +
            "  ⚙ Options ▾  – Toggle Auto-approve tools and Fleet (parallel sub-agents)\r\n" +
            "  Send         – Submit the current prompt (Ctrl+Enter)\r\n" +
            "  Stop         – Cancel an in-progress response\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── Menu bar ──────────────────────────────────────────\r\n", AppTheme.ColorMeta);

        AppendOutput("  Session\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "    📂 New Session…   Pick a workspace folder and start a fresh session\r\n" +
            "    📝 Summarize      Ask Copilot for a session summary\r\n" +
            "    🗑 Clear Output   Clear the output panel (session not reset)\r\n" +
            "    💤 Refresh ▸      Free context window:\r\n" +
            "       ⚡ Compact        In-place compaction; session ID preserved\r\n" +
            "       🔄 Restart        Save dream file + start fresh session w/ summary\r\n" +
            "       🆕 Fresh start    Discard all context; new session, same folder\r\n" +
            "    📋 Past Sessions… Browse persisted sessions to resume or delete\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("  References\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "    📄 Add File…      Attach one or more files to the next prompt\r\n" +
            "    📁 Add Folder…    Attach a folder to the next prompt\r\n" +
            "    List Agents…      Pick a custom agent; inserts @agent:name at caret\r\n" +
            "    List Skills…      Pick a skill; inserts @skill:name at caret\r\n" +
            "    List Prompts…     Pick a prompt template (prompts/*.md); attaches it like a file\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("  Tools\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "    ⚡ PowerShell     Open terminal in the project folder\r\n" +
            "    📂 File Explorer  Open File Explorer at the project folder\r\n" +
            "    💻 VS Code        Launch VS Code in the project folder\r\n" +
            "    🌳 Skill Tree…    Edit folders contributing skills/ and agents/\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("  Help\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "    ❓ Show Help      Show this guide (works without a folder open)\r\n" +
            "    About GoPilot    Version, build info, and credits\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── Prompt box ────────────────────────────────────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "  • Ctrl+Enter          Send the prompt\r\n" +
            "  • Drag & drop         Drop files or folders onto the box to attach\r\n" +
            "  • Paste images        Clipboard images are saved and attached on send\r\n" +
            "  • ▲ / ▼               Navigate prompt history (left edge buttons)\r\n" +
            "  • Right-click menu    Cut · Copy · Paste · Add File… · Add Folder… · List Agents… · List Skills… · List Prompts…\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── Reference tokens (inserted at the caret) ──────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "  @relative/path/file   Attach a file (chip also appears above output)\r\n" +
            "  @relative/path/folder Attach a folder\r\n" +
            "  @agent:name           Reference a custom agent\r\n" +
            "  @skill:name           Reference a skill\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── When Copilot asks permission ──────────────────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "  ✓ Allow           – Approve this one operation\r\n" +
            "  ✓ Approve Similar – Approve all operations of this type for the session\r\n" +
            "  ✗ Deny            – Reject; Copilot will adjust\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── What Copilot can do for you ───────────────────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "  • Read, write, and edit files.\r\n" +
            "  • Run shell commands and scripts.\r\n" +
            "  • Search and navigate the codebase.\r\n" +
            "  • Explain, refactor, and debug code.\r\n" +
            "  • Plan multi-step tasks before acting.\r\n" +
            "  • Spawn parallel agents (Fleet) for large jobs.\r\n" +
            "  • Call MCP tools and external services.\r\n" +
            "  • Fetch URLs and access memory.\r\n\r\n",
            AppTheme.ColorDefault);

        AppendOutput("── Tips ──────────────────────────────────────────────\r\n", AppTheme.ColorMeta);
        AppendOutput(
            "  • Use Plan mode for big tasks so you can review before Copilot acts.\r\n" +
            "  • Drag files onto the prompt box to attach them.\r\n" +
            "  • Session ▸ 📝 Summarize often to capture progress.\r\n" +
            "  • Session ▸ 📋 Past Sessions… to resume an earlier conversation.\r\n" +
            "  • Watch the Context meter in the status bar. At 85% GoPilot will offer to refresh.\r\n\r\n",
            AppTheme.ColorDefault);
    }

    private void ShowHelpAsync()
    {
        ShowHelp();
    }

    private async Task SendQuickCommandAsync(string prompt)
    {
        // Both an active connection AND a workspace are required. The CLI
        // can be connected without a workspace (e.g. after Past Sessions),
        // which would otherwise let quick commands create sessions with an
        // empty workspaceFolder in gopilot-sessions.json.
        if (!_copilot.IsConnected
            || string.IsNullOrEmpty(_copilot.WorkingDirectory))
        {
            MessageBox.Show(this,
                "Open a folder to connect to Copilot first.",
                "Not Connected",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await DispatchPromptAsync(prompt);
    }

    private async Task DispatchPromptAsync(
        string prompt,
        IReadOnlyList<string>? extraAttachments = null,
        (int OriginalChars, int CavemanChars)? cavemanStats = null)
    {
        _copilot.ActiveMode = comboBoxMode.SelectedItem?.ToString() ?? "Standard";
        _copilot.AutoApprove = menuOptionAutoApprove.Checked;
        _copilot.FleetMode = menuOptionFleet.Checked;

        // If a UI option change scheduled a deferred handoff, run it now BEFORE
        // the user's prompt so context survives mode/fleet switches automatically.
        if (_pendingHandoffReason != null
            && _copilot.IsConnected
            && _mainSessionId != null
            && !_refreshInProgress)
        {
            var reason = _pendingHandoffReason;
            _pendingHandoffReason = null;
            await PerformHandoffAsync(reason, waitForSeedAck: true);
        }

        _pendingCount++;
        UpdateWorkingState();
        SoundService.PlayPromptSent();

        try
        {
            var attachmentsCopy = _attachments.ToList();
            if (extraAttachments is { Count: > 0 })
                attachmentsCopy.AddRange(extraAttachments);
            await _copilot.SendMessageAsync(prompt, attachmentsCopy);

            // Echo user message
            if (_mainSessionId != null)
            {
                SetSessionDescriptionIfEmpty(_mainSessionId, prompt);
                // Raw tab only: the Rendered tab gets the structured User block via
                // WebViewAppendBlock below, so mirroring here would duplicate the prompt.
                AppendRaw($"\U0001f464 You: {prompt}\r\n\r\n", AppTheme.ColorUser);
                var userBlock = new OutputBlock(BlockKind.User)
                {
                    Label = "\U0001f464 You:",
                    Content = prompt,
                    IsComplete = true
                };
                _outputBlocks.Add(userBlock);
                WebViewAppendBlock(userBlock);

                // Mirror any pasted clipboard images as inline thumbnails beneath
                // the user echo. Image bytes still flow to the Copilot SDK via
                // AttachmentBlob (SendMessageAsync above); this is a
                // visual courtesy only -- the prompt text itself is unchanged,
                // so this does not increase upstream tokens.
                AppendPastedImagePreviews(userBlock, extraAttachments);

                // Phase 4.5: show animated "Thinking..." pill for the dead air
                // between Send and the very first event of the turn arriving back.
                // Removed automatically by AppendRenderedMessage on the first
                // non-BytesUpdate event for the main session.
                if (_activeThinkingId != null)
                {
                    WebViewRemoveThinking(_activeThinkingId);
                }
                _activeThinkingId = $"th{System.Threading.Interlocked.Increment(ref _nextThinkingId)}";
                WebViewAppendThinking(_activeThinkingId);

                // C2 - Caveman before/after meter, written immediately after the
                // user echo block so it visually attaches to the prompt it describes.
                // Only SendPromptAsync supplies stats, so STOP and auto-handoff
                // paths naturally skip this.
                if (cavemanStats is { } stats && stats.OriginalChars > 0)
                {
                    var saved = stats.OriginalChars - stats.CavemanChars;
                    var percent = (int)Math.Round(100.0 * saved / stats.OriginalChars);
                    AppendOutput(
                        $"\U0001f9b4 Caveman: {stats.OriginalChars} -> {stats.CavemanChars} chars ({percent:+0;-0;0}%, saved {saved})\r\n\r\n",
                        AppTheme.ColorMeta);
                }
            }
        }
        catch (Exception ex)
        {
            _pendingCount = Math.Max(0, _pendingCount - 1);
            UpdateWorkingState();
            if (_mainSessionId != null)
                AppendOutput($"\r\n❌ Error sending: {ex.Message}\r\n", AppTheme.ColorError);
        }
    }

    private void ClearActiveOutput()
    {
        if (richTextBoxOutput.TextLength == 0 && _outputBlocks.Count == 0) return;
        if (MessageBox.Show("Clear all output?", "Clear Output",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            richTextBoxOutput.Clear();
            _toolStartPositions.Clear();
            _subAgentStartPositions.Clear();
            _activeSubAgents.Clear();
            _activeSubAgentSessions.Clear();
            _streamingSessions.Clear();
            _subAgentWatchdog.Stop();
            _completedAgentCount = 0;
            _mainSessionIdle = false;
            // Clear the Rendered tab
            _outputBlocks.Clear();
            _streamingBlocks.Clear();
            _renderedSubAgentBlocks.Clear();
            ClearSectionTrackers();
            WebViewClearAll();
        }
    }

    private void OpenExplorer()
    {
        var dir = _copilot.WorkingDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("No session folder is open yet. Use 'Session > New Session...' first.",
                "No Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void OpenPowershell()
    {
        var dir = _copilot.WorkingDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("No session folder is open yet. Use 'Session > New Session...' first.",
                "No Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var scriptsPath = Path.Combine(Application.StartupPath, "scripts.ps1");
            var arguments = File.Exists(scriptsPath)
                ? $"-NoExit -Command \". '{scriptsPath.Replace("'", "''")}'\""
                : "-NoExit";

            var fileName = ResolvePowerShellExecutable();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open PowerShell:\n\n{ex.Message}",
                "PowerShell Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string ResolvePowerShellExecutable()
    {
        // Prefer PowerShell 7+ (pwsh.exe) so users get their modern profile (and
        // modules like posh-git that are typically installed there). Fall back to
        // the in-box Windows PowerShell 5.1 if pwsh isn't available.
        var pathExt = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathExt.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return "powershell.exe";
    }

    private void OpenVSCode()
    {
        // Note: the Copilot CLI's /ide command (which pairs the running session with
        // VS Code's Copilot extension so it can surface diffs and open files) is
        // documented as TUI-only in the Copilot SDK. There is no SDK RPC equivalent,
        // and slash commands are not dispatched through the prompt channel. This
        // button therefore only launches VS Code; live IDE pairing is not available
        // through GoPilot today.
        var dir = _copilot.WorkingDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("No session folder is open yet. Use 'Session > New Session...' first.",
                "No Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not launch VS Code:\n\n{ex.Message}\n\n" +
                "Make sure the 'code' command is on your PATH " +
                "(VS Code → Command Palette → 'Install code command in PATH').",
                "VS Code Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ApplyModeChangeAsync()
    {
        var prevMode = _copilot.ActiveMode;
        var mode = comboBoxMode.SelectedItem?.ToString() ?? "Standard";
        _copilot.ActiveMode = mode;

        // Autopilot implies auto-approve
        if (mode == "Autopilot" && !menuOptionAutoApprove.Checked)
            menuOptionAutoApprove.Checked = true;

        ApplyAutoApproveAvailability(mode);

        // Mode is baked into the session's system message at creation time.
        // Defer the session reset to the next send so we can summarise the
        // current conversation and seed it into the new session automatically.
        if (_copilot.IsConnected && _mainSessionId != null)
        {
            ScheduleHandoff($"Mode changed to {mode}");

            // Crossing the Autopilot boundary flips whether Auto-approve is
            // forced, so the effective permissions picture has changed even
            // if the AutoApprove toggle itself did not (already-checked case
            // does not re-fire CheckedChanged).
            bool prevAutopilot = string.Equals(prevMode, "Autopilot",
                StringComparison.Ordinal);
            bool nextAutopilot = string.Equals(mode, "Autopilot",
                StringComparison.Ordinal);
            if (prevAutopilot != nextAutopilot)
                EmitPermissionsStatus($"Mode -> {mode}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Gates the Auto-approve menu item based on the active mode.  Autopilot
    /// mode forces auto-approve at both the system-message layer and the SDK
    /// runtime layer (see <see cref="CopilotService.EffectiveApproveAll"/>),
    /// so the user-facing toggle has no effect there.  Disabling it -- and
    /// annotating its label -- prevents the silent UX trap where the user
    /// believes they have turned auto-approve off while it remains active.
    /// </summary>
    private void ApplyAutoApproveAvailability(string mode)
    {
        bool forced = string.Equals(mode, "Autopilot", StringComparison.Ordinal);
        menuOptionAutoApprove.Enabled = !forced;
        menuOptionAutoApprove.Text = forced
            ? "&Auto-approve tools (forced by Autopilot)"
            : "&Auto-approve tools";
    }

    /// <summary>
    /// Writes a single-line status banner to the output window summarising the
    /// effective permission gating for the connected workspace.  Called at
    /// session start and whenever any input affecting the picture changes
    /// (Auto-approve toggle, Mode change crossing the Autopilot boundary, or
    /// the Workspace Permissions dialog editing the active folder).  Silent
    /// when no workspace is connected.  Format:
    /// <c>[Permissions: Auto-approve ON | Workspace pre-approvals: shell, read]</c>
    /// </summary>
    private void EmitPermissionsStatus(string? reason = null)
    {
        var folder = _copilot.WorkingDirectory;
        if (string.IsNullOrEmpty(folder)) return;

        bool autoForced = string.Equals(_copilot.ActiveMode, "Autopilot",
            StringComparison.Ordinal);
        string autoState = autoForced
            ? "FORCED by Autopilot"
            : (_copilot.AutoApprove ? "ON" : "OFF");

        var kinds = CopilotPermissionsConfig.GetApprovedKinds(folder);
        string preApprovals = kinds.Count == 0
            ? "(none)"
            : string.Join(", ", kinds);

        string suffix = string.IsNullOrEmpty(reason) ? "" : $" -- {reason}";
        AppendOutput(
            $"[Permissions: Auto-approve {autoState} | Workspace pre-approvals: {preApprovals}{suffix}]\r\n",
            AppTheme.ColorMeta);
    }

    /// <summary>
    /// Marks the current session for an automatic summary-and-restart on the
    /// next prompt dispatch. Idempotent — repeated changes overwrite the reason.
    /// </summary>
    private void ScheduleHandoff(string reason)
    {
        _pendingHandoffReason = reason;
        AppendOutput(
            $"\r\n[{reason} — context will be summarised and carried into a new session on your next send]\r\n\r\n",
            AppTheme.ColorMeta);
    }

    // ── Context meter & session refresh ──────────────────────────────────────

    private void OnContextUsageChanged(ContextUsageEventArgs args)
    {
        var input = args.InputTokens;
        var max = args.MaxPromptTokens;
        var pct = args.Percent;

        string text;
        Color color;
        int barValue;
        Color barColor;

        if (max <= 0)
        {
            text = "Prompt: \u2014 / \u2014 (\u2014)";
            color = Color.FromArgb(148, 148, 148);
            barValue = 0;
            barColor = Color.FromArgb(96, 96, 96);
        }
        else if (input <= 0)
        {
            text = $"Prompt: 0 / {FormatTokens(max)} (0%)";
            color = Color.FromArgb(148, 220, 148); // green
            barValue = 0;
            barColor = Color.FromArgb(148, 220, 148);
        }
        else
        {
            text = $"Prompt: {FormatTokens(input)} / {FormatTokens(max)} ({pct:0}%)";
            color = pct < 60 ? Color.FromArgb(148, 220, 148)  // green
                  : pct < 85 ? Color.FromArgb(232, 200, 110)  // amber
                              : Color.FromArgb(240, 120, 120); // red
            barColor = color;
            barValue = (int)Math.Clamp(Math.Round(pct), 0, 100);
        }

        if (bIsFreeTierAutoOnly)
        {
            // Replace context-meter with an explanatory message
            toolStripProgressBarContext.Visible = false;
            toolStripStatusLabelContext.Text = "Free tier";
            toolStripStatusLabelContext.ToolTipText = "Using free Copilot tier — UI features limited";
        }
        else
        {
            toolStripStatusLabelContext.Text = text;
            toolStripStatusLabelContext.ForeColor = color;
            toolStripProgressBarContext.Value = barValue;
            toolStripProgressBarContext.ForeColor = barColor;
            toolTipMain.SetToolTip(statusStrip,
                max > 0
                    ? $"Prompt window usage: {input:N0} / {max:N0} prompt tokens ({pct:0.0}%)\nClick \ud83d\udca4 Refresh to free space."
                    : "Prompt window usage will appear here after the first response.");
        }

        UpdateRefreshButtonAffordance(max > 0 ? pct : 0);

        // Auto-prompt at the configured threshold, once per session, and only when
        // we are not already mid-refresh or sending a turn.
        if (!_autoRefreshPromptShown
            && !_refreshInProgress
            && _pendingCount == 0
            && pct >= AutoRefreshThresholdPercent
            && _copilot.IsConnected
            && _mainSessionId != null)
        {
            _autoRefreshPromptShown = true;
            // Defer to avoid blocking the event handler.
            BeginInvoke(new Action(PromptAutoRefresh));
        }
    }

    private static string FormatTokens(double tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000:0.#}M";
        if (tokens >= 1_000) return $"{tokens / 1_000:0.#}K";
        return ((int)tokens).ToString();
    }

    // Decorates the Refresh button with a glyph and tooltip that escalates with
    // context-window pressure: idle (\ud83d\udca4) below 60%, warning (\u26a0\ufe0f)
    // at 60%, critical (\ud83d\udd25) at 85%. Keeps the trailing dropdown caret.
    private void UpdateRefreshButtonAffordance(double pct)
    {
        string glyph;
        string tip;

        if (pct >= 85)
        {
            glyph = "\ud83d\udd25"; // fire
            tip = $"Prompt window at {pct:0}% \u2014 strongly recommend Compact or Restart now.";
        }
        else if (pct >= 60)
        {
            glyph = "\u26a0\ufe0f"; // warning sign
            tip = $"Prompt window at {pct:0}% \u2014 consider Compact or Restart soon.";
        }
        else
        {
            glyph = "\ud83d\udca4"; // zzz (idle)
            tip = "Free up context window \u2014 Compact (in place) or Restart with summary";
        }

        var newText = $"{glyph} Refresh";
        if (!string.Equals(menuSessionRefresh.Text, newText, StringComparison.Ordinal))
            menuSessionRefresh.Text = newText;
        menuSessionRefresh.ToolTipText = tip;
    }

    private void PromptAutoRefresh()
    {
        var pct = _copilot.CurrentMaxPromptTokens > 0
            ? (_copilot.CurrentInputTokens / _copilot.CurrentMaxPromptTokens) * 100.0
            : 0;

        var result = MessageBox.Show(this,
            $"Prompt window is at {pct:0}% — accuracy may start to degrade.\r\n\r\n" +
            "Refresh now?\r\n\r\n" +
            "  Yes  – Compact in place (fast, keeps session ID)\r\n" +
            "  No   – Restart with summary (clean window, new session)\r\n" +
            "  Cancel – Don't ask again this session",
            "Prompt Window Filling Up",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        switch (result)
        {
            case DialogResult.Yes:
                _ = RunCompactAsync();
                break;
            case DialogResult.No:
                _ = RunRestartWithSummaryAsync();
                break;
                // Cancel: stay silent for the rest of the session.
        }
    }

    private void ShowRefreshMenu()
    {
        // Programmatic equivalent of the menu drop-down (kept for any callers that
        // want to surface the same options outside the menu bar). Drops the
        // submenu open at the cursor.
        menuSessionRefresh.ShowDropDown();
    }

    /// <summary>
    /// Discards the current session entirely — no summary, no seed — and opens
    /// a fresh session rooted at the same folder.  Behaves like choosing
    /// Session > New Session... on the existing path: reconnects, then offers
    /// to read the project's README.
    /// </summary>
    private async Task RunFreshStartAsync()
    {
        if (_refreshInProgress) return;
        if (_copilot.WorkingDirectory == null)
        {
            MessageBox.Show(this, "No active workspace. Open a folder first.",
                "Nothing to Refresh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "Start a brand-new session in this folder?\r\n\r\n" +
            "All current conversation context will be discarded — no summary will be carried over.\r\n\r\n" +
            "You will be offered the chance to have the README read, " +
            "just like opening the folder fresh.",
            "Fresh Start", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.OK) return;

        _refreshInProgress = true;
        var oldText = menuSessionRefresh.Text;
        menuSessionRefresh.Enabled = false;
        menuSessionRefresh.Text = "⏳ Fresh start…";
        SetSendingState(true);

        var workingDir = _copilot.WorkingDirectory;

        try
        {
            AppendOutput("\r\n🆕 Discarding context and opening a fresh session…\r\n\r\n", AppTheme.ColorMeta);

            if (_copilot.IsConnected)
                await _copilot.ResetSessionAsync();

            _mainSessionId = null;
            ResetSessionTrackingState();

            // Sync UI state to service before session creation so the system message is correct
            _copilot.ActiveMode = comboBoxMode.SelectedItem?.ToString() ?? "Standard";
            _copilot.AutoApprove = menuOptionAutoApprove.Checked;
            _copilot.FleetMode = menuOptionFleet.Checked;

            // Let the user review/curate discovered MCP servers before the fresh
            // session is built (so unwanted servers never load).
            ReviewMcpServersBeforeSession();

            await _copilot.EnsureSessionAsync();

            AppendOutput("─────────── session refreshed ───────────\r\n\r\n", AppTheme.ColorMeta);
            _autoRefreshPromptShown = false;

            await OfferReadReadmeAsync(workingDir);
        }
        catch (Exception ex)
        {
            AppendOutput($"[Fresh start failed: {ex.Message}]\r\n\r\n", AppTheme.ColorError);
            MessageBox.Show(this, $"Fresh start failed:\r\n\r\n{ex.Message}",
                "Refresh Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            menuSessionRefresh.Text = oldText;
            menuSessionRefresh.Enabled = true;
            SetSendingState(false);
            _refreshInProgress = false;
        }
    }

    private async Task RunCompactAsync()
    {
        if (_refreshInProgress) return;
        if (!_copilot.IsConnected || _mainSessionId == null)
        {
            MessageBox.Show(this, "No active session to refresh. Open a folder and send at least one message first.",
                "Nothing to Refresh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _refreshInProgress = true;
        var oldText = menuSessionRefresh.Text;
        menuSessionRefresh.Enabled = false;
        menuSessionRefresh.Text = "⏳ Compacting…";
        SetSendingState(true);

        try
        {
            AppendOutput("\r\n💤 Compacting session in place…\r\n\r\n", AppTheme.ColorMeta);

            var ok = await _copilot.CompactSessionAsync();
            if (ok)
            {
                AppendOutput("─────────── session refreshed ───────────\r\n\r\n", AppTheme.ColorMeta);
                _autoRefreshPromptShown = false; // allow another nag if context fills again
            }
            else
            {
                AppendOutput(
                    "[Compact failed — falling back to Restart with summary]\r\n\r\n",
                    AppTheme.ColorMeta);
                _refreshInProgress = false; // RunRestart will reacquire the gate
                menuSessionRefresh.Text = oldText;
                menuSessionRefresh.Enabled = true;
                SetSendingState(false);
                await RunRestartWithSummaryAsync();
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Compact failed:\r\n\r\n{ex.Message}",
                "Refresh Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            menuSessionRefresh.Text = oldText;
            menuSessionRefresh.Enabled = true;
            SetSendingState(false);
            _refreshInProgress = false;
        }
    }

    // Shared summary prompt for both manual and automatic handoffs. The model is
    // told NOT to use tools so the capture is a pure conversation distillation.
    private const string HandoffSummaryPrompt =
        """
        Write a brief session-resume note in Markdown — use ONLY what is already in our conversation history. Do NOT use any tools, do NOT read any files or directories.

        Structure it as:

        # Session Resume

        ## Goal
        One or two sentences: what we set out to accomplish.

        ## What Was Done
        Short bullet list of the key things completed or decided this session.

        ## Current State
        One paragraph describing exactly where things stand right now.

        ## Next Step
        The single most important thing to do when resuming.

        ## Context to Remember
        Any non-obvious decisions, constraints, or gotchas a new session needs to know.

        Keep the whole document under one page. Draw entirely from our conversation — do not invoke tools.
        """;

    private const string HandoffSeedPrompt =
        "I am providing a session resume document from our previous session in this same workspace. " +
        "Please read it and confirm you understand the context so we can continue where we left off.";

    private Task RunRestartWithSummaryAsync()
        => PerformHandoffAsync("user requested", waitForSeedAck: false);

    /// <summary>
    /// Captures a conversation summary from the current session, persists it to
    /// the per-workspace <c>dreams\</c> folder under
    /// <c>%LOCALAPPDATA%\GoPilot\workspaces\&lt;key&gt;\</c>, tears the session
    /// down, opens a fresh one in the same workspace, and seeds it with the
    /// summary.
    /// </summary>
    /// <param name="reason">Short human-readable trigger (shown in the output panel).</param>
    /// <param name="waitForSeedAck">When true, awaits the assistant's acknowledgement
    /// of the seed before returning.  Used by automatic handoffs so the user's next
    /// prompt arrives after the new session has loaded the context.</param>
    /// <returns>True on success.  On failure the active session is reset so the next
    /// send still works (without preserved context).</returns>
    private async Task<bool> PerformHandoffAsync(string reason, bool waitForSeedAck)
    {
        if (_refreshInProgress) return false;
        if (!_copilot.IsConnected || _mainSessionId == null || _copilot.WorkingDirectory == null)
        {
            // Caller-driven UI flow (manual button) shows a dialog; automatic flow stays silent.
            if (!waitForSeedAck)
            {
                MessageBox.Show(this, "No active session to refresh. Open a folder and send at least one message first.",
                    "Nothing to Refresh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return false;
        }

        _refreshInProgress = true;
        var oldText = menuSessionRefresh.Text;
        menuSessionRefresh.Enabled = false;
        menuSessionRefresh.Text = "⏳ Handoff…";
        SetSendingState(true);

        try
        {
            AppendOutput($"\r\n💤 Capturing session summary — {reason}…\r\n\r\n", AppTheme.ColorMeta);

            var summary = await _copilot.SendAndCaptureResponseAsync(HandoffSummaryPrompt, TimeSpan.FromMinutes(5));

            // Guard at start of method ensures WorkingDirectory != null,
            // therefore WorkspaceDataPath is also non-null here.
            var dreamsDir = Path.Combine(_copilot.WorkspaceDataPath!, "dreams");
            Directory.CreateDirectory(dreamsDir);
            var dreamPath = Path.Combine(dreamsDir, $"dream-{DateTime.Now:yyyy-MM-dd-HHmmss}.md");

            var model = comboBoxModel.SelectedItem?.ToString() ?? string.Empty;
            var mode = comboBoxMode.SelectedItem?.ToString() ?? string.Empty;
            var metadata =
                $"\r\n<!-- gopilot-model: {model} -->\r\n" +
                $"<!-- gopilot-mode: {mode} -->\r\n" +
                $"<!-- gopilot-source: dream -->\r\n" +
                $"<!-- gopilot-reason: {reason} -->\r\n";

            await File.WriteAllTextAsync(dreamPath, summary + metadata);

            AppendOutput($"[Dream saved: {dreamPath}]\r\n", AppTheme.ColorMeta);
            AppendOutput("💤 Opening fresh session in this workspace…\r\n\r\n", AppTheme.ColorMeta);

            // Tear down + recreate so the new session honours the latest mode/fleet.
            await _copilot.ResetSessionAsync();
            await _copilot.EnsureSessionAsync();

            // Seed the new session.  When the caller is about to send a user prompt,
            // we wait for the assistant's ACK so the prompt is interpreted with full context.
            var seedFull = HandoffSeedPrompt + "\r\n\r\n" + summary;
            if (waitForSeedAck)
                await _copilot.SendAndCaptureResponseAsync(seedFull, TimeSpan.FromMinutes(2));
            else
                await _copilot.SendMessageAsync(seedFull, Array.Empty<string>());

            AppendOutput("─────────── session refreshed ───────────\r\n\r\n", AppTheme.ColorMeta);
            _autoRefreshPromptShown = false;
            return true;
        }
        catch (Exception ex)
        {
            AppendOutput($"[Handoff failed: {ex.Message} — falling back to clean restart]\r\n\r\n", AppTheme.ColorError);
            // Fall back to a plain reset so the user's next send still works,
            // even though context will be lost.
            try
            {
                await _copilot.ResetSessionAsync();
                _mainSessionId = null;
                ResetSessionTrackingState();
            }
            catch { /* best-effort */ }
            return false;
        }
        finally
        {
            menuSessionRefresh.Text = oldText;
            menuSessionRefresh.Enabled = true;
            SetSendingState(false);
            _refreshInProgress = false;
        }
    }

    /// <summary>
    /// When a workspace folder is opened, look for README.md (preferred) then
    /// README.txt in the project root. If found, prompt the user for permission
    /// to read it so GoPilot can better understand the project. The prompt
    /// includes an option to first preview the file in VS Code.
    /// </summary>
    private async Task OfferReadReadmeAsync(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot)) return;

        // Priority order: README.md before README.txt. Windows file system
        // lookups are case-insensitive, so this also matches Readme.md, etc.
        string? readmePath = null;
        foreach (var name in new[] { "README.md", "README.txt" })
        {
            var candidate = Path.Combine(projectRoot, name);
            if (File.Exists(candidate))
            {
                readmePath = candidate;
                break;
            }
        }

        if (readmePath == null) return;

        var fileName = Path.GetFileName(readmePath);

        while (true)
        {
            using var dialog = new ReadmePromptDialog(fileName);
            dialog.ShowDialog(this);

            switch (dialog.Result)
            {
                case ReadmePromptResult.Yes:
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(readmePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            $"Could not read {fileName}:\r\n\r\n{ex.Message}",
                            "Read Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // When the local LLM filter is online, reduce and summarize the
                    // README offline first so the cloud receives a compact digest
                    // instead of the full file. Best-effort: any failure forwards the
                    // full contents unchanged.
                    var readmeBody = content;
                    var readmeSummarized = false;
                    if (_localFilter.Enabled && _localFilter.Available)
                    {
                        var summary = await _localFilter.SummarizeAsync(fileName, content);
                        if (summary.Mode == LocalFilterMode.Minimized
                            && !string.IsNullOrWhiteSpace(summary.Prompt))
                        {
                            readmeBody = summary.Prompt;
                            readmeSummarized = true;
                            var saved = summary.OriginalChars - summary.FinalChars;
                            AppendOutput(
                                $"\U0001f9e0 Local ({summary.ModelLabel}): summarized {fileName} " +
                                $"{summary.OriginalChars} -> {summary.FinalChars} chars (saved {saved}), " +
                                "forwarding digest to cloud\r\n\r\n",
                                AppTheme.ColorMeta);
                        }
                        else if (!string.IsNullOrEmpty(summary.Note))
                        {
                            AppendOutput(
                                $"\U0001f9e0 Local: {fileName} summary skipped ({summary.Note}); " +
                                "sending full file to cloud\r\n\r\n",
                                AppTheme.ColorMeta);
                        }
                    }

                    AppendOutput($"[Sharing {fileName} with Copilot for project context]\r\n\r\n",
                        AppTheme.ColorMeta);

                    var readmeIntro = readmeSummarized
                        ? $"Here is a local-model summary of '{fileName}' from the workspace root. "
                        : $"Here is the contents of '{fileName}' from the workspace root. ";
                    await DispatchPromptAsync(
                        readmeIntro +
                        "Please read it to better understand the project we are working on, " +
                        "then briefly confirm you have done so.\r\n\r\n" +
                        $"```\r\n{readmeBody}\r\n```");
                    return;

                case ReadmePromptResult.OpenInVSCode:
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "code",
                            Arguments = $"\"{readmePath}\"",
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            $"Could not open {fileName} in VS Code:\r\n\r\n{ex.Message}\r\n\r\n" +
                            "Make sure the 'code' command is on your PATH.",
                            "VS Code Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    // Re-prompt so the user can decide after previewing.
                    continue;

                case ReadmePromptResult.No:
                default:
                    return;
            }
        }
    }

    private async Task OpenFolderAndConnectAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select workspace folder...",
            UseDescriptionForTitle = true,
            SelectedPath = _copilot.WorkingDirectory ?? "",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        await ConnectToFolderAsync(dialog.SelectedPath);
    }

    private async Task ConnectToFolderAsync(string folderPath)
    {
        // If already connected, tear down first so the new CWD takes effect
        if (_copilot.IsConnected)
        {
            await _copilot.DisposeAsync();
            _copilot.Reset();
        }

        ResetSessionTrackingState();

        if (!EnsureFolderTrusted(folderPath)) return;

        _copilot.WorkingDirectory = folderPath;
        menuSessionNew.Enabled = false;
        toolStripStatusLabelSession.Text = folderPath;
        UpdateTitleBar(folderPath);

        try
        {
            // Sync UI state to service before session creation so the system message is correct
            _copilot.ActiveMode = comboBoxMode.SelectedItem?.ToString() ?? "Standard";
            _copilot.AutoApprove = menuOptionAutoApprove.Checked;
            _copilot.FleetMode = menuOptionFleet.Checked;

            // First action of a new session: let the user review/curate the MCP
            // servers discovered across the search folders (workspace, user, app)
            // before the session is created, so unwanted servers never load.
            ReviewMcpServersBeforeSession();

            await _copilot.EnsureSessionAsync();
            var version = await _copilot.GetVersionAsync();
            if (!string.IsNullOrEmpty(version))
                toolStripStatusLabelVersion.Text = $"v{version}";

            if (_copilot.ScratchpadPath != null)
                AppendOutput($"[Scratchpad: {_copilot.ScratchpadPath}]\r\n", AppTheme.ColorMeta);

            EmitPermissionsStatus();

            // Report which instruction tiers were loaded
            foreach (var (label, folder) in _copilot.GetTierFolders())
            {
                var instructionsPath = System.IO.Path.Combine(folder, "gopilot-instructions.md");
                var agentsDir = System.IO.Path.Combine(folder, "agents");
                var skillsDir = System.IO.Path.Combine(folder, "skills");

                var hasMd = System.IO.File.Exists(instructionsPath);
                var agentCount = System.IO.Directory.Exists(agentsDir)
                    ? System.IO.Directory.GetFiles(agentsDir, "*.md", System.IO.SearchOption.TopDirectoryOnly).Length
                    : 0;
                var hasSkills = System.IO.Directory.Exists(skillsDir);

                var parts = new System.Text.StringBuilder();
                if (hasMd) parts.Append("instructions");
                if (agentCount > 0)
                {
                    if (parts.Length > 0) parts.Append(", ");
                    parts.Append($"{agentCount} agent{(agentCount == 1 ? "" : "s")}");
                }
                if (hasSkills)
                {
                    if (parts.Length > 0) parts.Append(", ");
                    parts.Append("skills");
                }

                var summary = parts.Length > 0 ? parts.ToString() : "no files";
                AppendOutput($"[{label} tier: {folder} ({summary})]\r\n", AppTheme.ColorMeta);
            }

            AppendOutput("\r\n", AppTheme.ColorMeta);

            await OfferReadReadmeAsync(folderPath);
        }
        catch (Exception ex)
        {
            menuSessionNew.Enabled = true;
            MessageBox.Show(
                $"Failed to connect to Copilot CLI:\n\n{ex.Message}\n\n" +
                "Make sure 'copilot' is installed and authenticated (run 'copilot auth login').",
                "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            menuSessionNew.Enabled = true;
        }
    }

    // ── Session persistence ──────────────────────────────────────────────────

    /// <summary>
    /// Records the current UI settings against the given session ID so the
    /// session can be restored with the correct workspace, model, and mode.
    /// </summary>
    private void SaveSessionMetadata(string sessionId)
    {
        var existing = _sessionStore.Find(sessionId);

        // Prefer the live WorkingDirectory, but never overwrite a previously
        // stored non-empty workspace with an empty one (sessions can be
        // created before a folder is opened, and we don't want to lose the
        // workspace later when SaveSessionMetadata is re-invoked with a
        // null WorkingDirectory).
        var liveWorkspace = _copilot.WorkingDirectory ?? "";
        var workspace = !string.IsNullOrEmpty(liveWorkspace)
            ? liveWorkspace
            : existing?.WorkspaceFolder ?? "";

        _sessionStore.Save(new SessionMetadataEntry
        {
            SessionId = sessionId,
            WorkspaceFolder = workspace,
            Model = comboBoxModel.SelectedItem?.ToString() ?? "",
            Mode = comboBoxMode.SelectedItem?.ToString() ?? "Standard",
            Fleet = menuOptionFleet.Checked,
            AutoApprove = menuOptionAutoApprove.Checked,
            CreatedAt = existing?.CreatedAt is { } prior && prior != default
                ? prior
                : DateTime.Now,
            Description = existing?.Description ?? "",
        });
    }

    /// <summary>
    /// Records a brief description (typically the first user prompt) against
    /// the given session so it can be shown in the Past Sessions picker. No-op
    /// if a description has already been stored for this session.
    /// </summary>
    private void SetSessionDescriptionIfEmpty(string sessionId, string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return;

        // The metadata row is normally seeded by OnSessionCreated, but that
        // handler is queued via BeginInvoke and may not have run yet on the
        // first prompt of a brand-new session. Create a stub entry on the
        // fly so the description is never silently dropped due to ordering.
        var existing = _sessionStore.Find(sessionId);
        if (existing == null)
        {
            existing = new SessionMetadataEntry
            {
                SessionId = sessionId,
                WorkspaceFolder = _copilot.WorkingDirectory ?? "",
                Model = comboBoxModel.SelectedItem?.ToString() ?? "",
                Mode = comboBoxMode.SelectedItem?.ToString() ?? "Standard",
                Fleet = menuOptionFleet.Checked,
                AutoApprove = menuOptionAutoApprove.Checked,
                CreatedAt = DateTime.Now,
                Description = "",
            };
        }
        else if (!string.IsNullOrWhiteSpace(existing.Description))
        {
            return;
        }

        existing.Description = SummariseForDescription(description);
        _sessionStore.Save(existing);
    }

    /// <summary>
    /// Trims and condenses a prompt down to one or two sentences suitable for
    /// the Description column in the Past Sessions list.
    /// </summary>
    private static string SummariseForDescription(string text)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(
            text.Trim(), @"\s+", " ");
        const int max = 240;
        if (collapsed.Length <= max) return collapsed;
        return collapsed.Substring(0, max - 1).TrimEnd() + "\u2026";
    }

    /// <summary>
    /// Builds the list of rows for the session picker dialog by merging SDK-known
    /// sessions with locally stored metadata.
    /// </summary>
    private async Task<List<SessionListDialog.SessionRow>> BuildSessionRowsAsync()
    {
        // Start the client if needed (but don't require a workspace)
        await _copilot.EnsureStartedAsync();
        var liveIds = await _copilot.ListPersistedSessionsAsync();
        var liveSet = new HashSet<string>(liveIds, StringComparer.Ordinal);

        // Prune stale metadata entries
        _sessionStore.Prune(liveSet);

        var rows = new List<SessionListDialog.SessionRow>();
        foreach (var id in liveIds)
        {
            var meta = _sessionStore.Find(id);
            rows.Add(new SessionListDialog.SessionRow
            {
                SessionId = id,
                Workspace = meta?.WorkspaceFolder ?? "",
                Model = meta?.Model ?? "",
                Mode = meta?.Mode ?? "",
                CreatedAt = meta?.CreatedAt ?? default,
                Description = meta?.Description ?? "",
                Metadata = meta,
            });
        }

        // Most recent first
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return rows;
    }

    private async Task BrowsePastSessionsAsync()
    {
        List<SessionListDialog.SessionRow> rows;
        try
        {
            rows = await BuildSessionRowsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not list sessions:\r\n\r\n{ex.Message}",
                "Session List Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (rows.Count == 0)
        {
            MessageBox.Show(this,
                "No persisted sessions found.",
                "Past Sessions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SessionListDialog(
            rows,
            _mainSessionId,
            DeleteSessionsFromDialogAsync);

        var result = dialog.ShowDialog(this);

        if (result == DialogResult.OK
            && dialog.ResumeSelected is { } resumeRow)
        {
            await ResumePersistedSessionAsync(resumeRow.SessionId, resumeRow.Metadata);
        }
    }

    /// <summary>
    /// Delete callback handed to <see cref="SessionListDialog"/>. Returns the
    /// list of session IDs that failed to delete (empty on full success).
    /// </summary>
    private async Task<IReadOnlyList<string>> DeleteSessionsFromDialogAsync(
        IReadOnlyList<string> ids)
    {
        var failed = new List<string>();
        int deleted = 0;
        foreach (var id in ids)
        {
            try
            {
                await _copilot.DeletePersistedSessionAsync(id);
                _sessionStore.Remove(id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed.Add(id);
                AppendOutput($"[Failed to delete {id}: {ex.Message}]\r\n", AppTheme.ColorError);
            }
        }

        if (deleted > 0)
        {
            AppendOutput(
                $"[Deleted {deleted} session{(deleted == 1 ? "" : "s")}]\r\n\r\n",
                AppTheme.ColorMeta);
        }

        return failed;
    }

    private async Task ResumePersistedSessionAsync(
        string sessionId, SessionMetadataEntry? metadata)
    {
        SetSendingState(true);

        try
        {
            // Determine workspace folder
            var workspace = metadata?.WorkspaceFolder;
            if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
            {
                // If metadata has no workspace or it doesn't exist, ask the user
                using var folderDlg = new FolderBrowserDialog
                {
                    Description = "Select the workspace folder for this session",
                    UseDescriptionForTitle = true,
                    SelectedPath = workspace ?? "",
                };
                if (folderDlg.ShowDialog(this) != DialogResult.OK)
                {
                    SetSendingState(false);
                    return;
                }
                workspace = folderDlg.SelectedPath;
            }

            // Restore UI settings from metadata before connecting
            if (metadata != null)
            {
                // Restore model
                if (!string.IsNullOrEmpty(metadata.Model))
                {
                    var modelIdx = comboBoxModel.Items.Cast<string>()
                        .Select((m, i) => (m, i))
                        .Where(x => x.m.Equals(metadata.Model, StringComparison.OrdinalIgnoreCase))
                        .Select(x => (int?)x.i)
                        .FirstOrDefault();
                    if (modelIdx.HasValue)
                        comboBoxModel.SelectedIndex = modelIdx.Value;
                }

                // Restore mode
                if (!string.IsNullOrEmpty(metadata.Mode))
                {
                    var modeIdx = comboBoxMode.Items.Cast<string>()
                        .Select((m, i) => (m, i))
                        .Where(x => x.m.Equals(metadata.Mode, StringComparison.OrdinalIgnoreCase))
                        .Select(x => (int?)x.i)
                        .FirstOrDefault();
                    if (modeIdx.HasValue)
                        comboBoxMode.SelectedIndex = modeIdx.Value;
                }

                // Restore fleet and auto-approve
                menuOptionFleet.Checked = metadata.Fleet;
                menuOptionAutoApprove.Checked = metadata.AutoApprove;
            }

            // Tear down existing connection and reconnect with the session's workspace
            if (_copilot.IsConnected)
            {
                await _copilot.DisposeAsync();
                _copilot.Reset();
            }

            ResetSessionTrackingState();

            if (!EnsureFolderTrusted(workspace)) return;

            _copilot.WorkingDirectory = workspace;
            menuSessionNew.Enabled = false;
            toolStripStatusLabelSession.Text = workspace;
            UpdateTitleBar(workspace);

            // Sync UI state to service
            _copilot.ActiveMode = comboBoxMode.SelectedItem?.ToString() ?? "Standard";
            _copilot.AutoApprove = menuOptionAutoApprove.Checked;
            _copilot.FleetMode = menuOptionFleet.Checked;

            AppendOutput($"\r\n📋 Resuming session: {sessionId}\r\n\r\n", AppTheme.ColorMeta);

            // Resume the persisted session (instead of creating a new one)
            await _copilot.ResumePersistedSessionAsync(sessionId);

            var version = await _copilot.GetVersionAsync();
            if (!string.IsNullOrEmpty(version))
                toolStripStatusLabelVersion.Text = $"v{version}";

            // Replay conversation history into the output panel
            await ReplaySessionHistoryAsync();

            AppendOutput("─────────── session resumed ───────────\r\n\r\n", AppTheme.ColorMeta);
        }
        catch (Exception ex)
        {
            AppendOutput($"\r\n❌ Resume failed: {ex.Message}\r\n\r\n", AppTheme.ColorError);
            MessageBox.Show(this,
                $"Failed to resume session:\r\n\r\n{ex.Message}",
                "Resume Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            menuSessionNew.Enabled = true;
            SetSendingState(false);
        }
    }

    /// <summary>
    /// Fetches the conversation history from the resumed session and replays
    /// it into both the Raw and Rendered output panels so the user can see
    /// what was discussed previously.
    /// </summary>
    private async Task ReplaySessionHistoryAsync()
    {
        var messages = await _copilot.GetSessionMessagesAsync();
        if (messages.Count == 0) return;

        AppendOutput("[Restoring conversation history...]\r\n\r\n", AppTheme.ColorMeta);

        foreach (var (type, content) in messages)
        {
            switch (type.ToLowerInvariant())
            {
                case "user":
                    // Raw tab only -- the structured User block below populates the Rendered tab.
                    AppendRaw($"\U0001f464 You: {Truncate(content, 500)}\r\n\r\n",
                        AppTheme.ColorUser);
                    var userBlock = new OutputBlock(BlockKind.User)
                    {
                        Label = "\U0001f464 You:",
                        Content = Truncate(content, 500),
                        IsComplete = true,
                    };
                    _outputBlocks.Add(userBlock);
                    WebViewAppendBlock(userBlock);
                    break;

                case "assistant":
                    // Raw tab only -- structured Assistant block below populates the Rendered tab.
                    AppendRaw($"\U0001f916 Assistant:\r\n{content}\r\n\r\n",
                        AppTheme.ColorAssistant);
                    var assistantBlock = new OutputBlock(BlockKind.Assistant)
                    {
                        Label = "\U0001f916 Assistant:",
                        Content = content,
                        IsComplete = true,
                    };
                    _outputBlocks.Add(assistantBlock);
                    WebViewAppendBlock(assistantBlock);
                    break;

                default:
                    // Tool calls, system messages, etc. — show as meta
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        AppendOutput($"[{type}] {Truncate(content, 200)}\r\n",
                            AppTheme.ColorMeta);
                    }
                    break;
            }
        }

        AppendOutput("\r\n", AppTheme.ColorMeta);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    // ── Skill Tree configuration ──────────────────────────────────────────────

    private void EditSkillTree()
    {
        using var dialog = new SkillTreeDialog(_settings.SkillTreeFolders);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Replace the persisted list and the live service list with the edited one.
        _settings.SkillTreeFolders = new List<string>(dialog.Folders);
        _copilot.SkillTreeFolders = _settings.SkillTreeFolders;

        UpdateSkillTreeTooltip();

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save settings to gopilot.ini:\n\n{ex.Message}",
                "Settings Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Skill Tree contents are baked into the session at creation time
        // (via SkillDirectories / CustomAgents).  Match the mode/fleet pattern:
        // schedule a deferred summary-and-restart so context survives the change.
        if (_copilot.IsConnected && _mainSessionId != null)
            ScheduleHandoff("Skill Tree changed");
    }

    private void UpdateSkillTreeTooltip()
    {
        var folders = _settings.SkillTreeFolders;
        string tip;
        if (folders.Count == 0)
        {
            tip = "Skill Tree: empty\n(click to edit)";
        }
        else
        {
            var lines = new System.Text.StringBuilder();
            lines.Append($"Skill Tree ({folders.Count} folder{(folders.Count == 1 ? "" : "s")}):");
            foreach (var f in folders)
                lines.Append("\n  • ").Append(f);
            lines.Append("\n(click to edit)");
            tip = lines.ToString();
        }
        menuToolsSkillTree.ToolTipText = tip;
    }

    // ── Options badges (drop-down + on-button strip) ─────────────────────────

    private Bitmap? _badgeCaveman;
    private Bitmap? _badgeAutoApprove;
    private Bitmap? _badgeFleet;
    private Bitmap? _badgeShowSteps;
    private Bitmap? _badgeLocalFilter;
    private Bitmap? _badgeToolSearch;

    // Cached ImageAttributes used to render an Options badge in the
    // "off" (greyed-out) state. Built once in InitializeOptionIcons and
    // reused on every paint; reuse is safe because ImageAttributes is
    // stateless across DrawImage calls.
    private System.Drawing.Imaging.ImageAttributes? _disabledBadgeAttr;

    /// <summary>
    /// Builds the badge bitmaps for the four Options menu items (in full
    /// colour via WPF emoji rendering, with a flat coloured-square
    /// monochrome fallback), assigns them as Image-margin icons on the
    /// drop-down, and wires the Options button up for owner-drawn
    /// painting so the button always shows the full set of option
    /// badges -- in colour when enabled, greyed-out when disabled.
    ///
    /// Must be called after InitializeComponent (which creates the
    /// controls) but BEFORE the constructor sets the initial Checked
    /// state of each menu item, so the first paint reflects the
    /// persisted preferences.
    /// </summary>
    private void InitializeOptionIcons()
    {
        _badgeAutoApprove = OptionIconRenderer.LoadEmbeddedBadge(
                                OptionIconRenderer.AutoApproveResource)
                            ?? OptionIconRenderer.CreateSquareBadge(
                                OptionIconRenderer.AutoApproveSquare,
                                OptionIconRenderer.AutoApproveGlyph);
        _badgeFleet = OptionIconRenderer.LoadEmbeddedBadge(
                                OptionIconRenderer.FleetResource)
                            ?? OptionIconRenderer.CreateSquareBadge(
                                OptionIconRenderer.FleetSquare,
                                OptionIconRenderer.FleetGlyph);
        _badgeCaveman = OptionIconRenderer.LoadEmbeddedBadge(
                                OptionIconRenderer.CavemanResource)
                            ?? OptionIconRenderer.CreateSquareBadge(
                                OptionIconRenderer.CavemanSquare,
                                OptionIconRenderer.CavemanGlyph);
        _badgeShowSteps = OptionIconRenderer.LoadEmbeddedBadge(
                                OptionIconRenderer.ShowStepsResource)
                            ?? OptionIconRenderer.CreateSquareBadge(
                                OptionIconRenderer.ShowStepsSquare,
                                OptionIconRenderer.ShowStepsGlyph);
        _badgeLocalFilter = OptionIconRenderer.CreateSquareBadge(
                                OptionIconRenderer.LocalFilterSquare,
                                OptionIconRenderer.LocalFilterGlyph);
        _badgeToolSearch = OptionIconRenderer.CreateSquareBadge(
                                OptionIconRenderer.ToolSearchSquare,
                                OptionIconRenderer.ToolSearchGlyph);

        menuSessionCaveman.Image = _badgeCaveman;
        menuOptionAutoApprove.Image = _badgeAutoApprove;
        menuOptionFleet.Image = _badgeFleet;
        menuSessionShowSteps.Image = _badgeShowSteps;
        menuOptionLocalFilter.Image = _badgeLocalFilter;
        menuOptionToolSearch.Image = _badgeToolSearch;

        // Show the 18x18 badges at their native size in the menu margin
        // instead of letting WinForms downscale them to the default 16x16.
        contextMenuOptions.ImageScalingSize = new Size(18, 18);

        // Build the greyscale + reduced-alpha colour matrix used by the
        // paint handler to render "off" badges. The luminance weights
        // (0.30/0.59/0.11) are the standard ITU-R BT.601 coefficients;
        // the alpha row multiplies source alpha by 0.40 so disabled
        // badges visibly recede while still hinting at what the option
        // would look like when active.
        var greyMatrix = new System.Drawing.Imaging.ColorMatrix(new[]
        {
            new float[] { 0.30f, 0.30f, 0.30f, 0f,    0f },
            new float[] { 0.59f, 0.59f, 0.59f, 0f,    0f },
            new float[] { 0.11f, 0.11f, 0.11f, 0f,    0f },
            new float[] { 0f,    0f,    0f,    0.40f, 0f },
            new float[] { 0f,    0f,    0f,    0f,    1f },
        });
        _disabledBadgeAttr = new System.Drawing.Imaging.ImageAttributes();
        _disabledBadgeAttr.SetColorMatrix(greyMatrix);

        // Keep the on-button badge strip in sync with the menu state.
        menuOptionAutoApprove.CheckedChanged += (_, _) => buttonOptions.Invalidate();
        menuOptionFleet.CheckedChanged += (_, _) => buttonOptions.Invalidate();
        menuSessionCaveman.CheckedChanged += (_, _) => buttonOptions.Invalidate();
        menuSessionShowSteps.CheckedChanged += (_, _) => buttonOptions.Invalidate();
        menuOptionLocalFilter.CheckedChanged += (_, _) => buttonOptions.Invalidate();
        menuOptionToolSearch.CheckedChanged += (_, _) => buttonOptions.Invalidate();

        // Clear the design-time caption so our Paint handler owns the entire
        // button surface (otherwise the base button paints "⚙ Options ▾" and
        // our badges overlap the text).
        buttonOptions.Text = "";
        buttonOptions.Paint += OnOptionsButtonPaint;
    }

    /// <summary>
    /// Owner-draws the Options button: "Options" on the left, a strip of
    /// badges for every option in the middle (enabled options in full
    /// colour, disabled options greyed-out via _disabledBadgeAttr), and
    /// a chevron on the right. The badge order is fixed (Auto-approve,
    /// Fleet, Caveman, Show Steps, Local filter, Tool Search) so the user
    /// can read each option's state by position regardless of which order
    /// they toggled them.
    /// </summary>
    private void OnOptionsButtonPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var btn = buttonOptions;
        using var font = new Font("Segoe UI", 9f);

        const string label = "Options";
        const string chevron = "\u25BE";
        const int badgeSz = 18;
        const int gap = 6;
        const int badgeGap = 2;

        // Fixed order, with the current Checked state of each menu item.
        // Any badge bitmap that failed to load is skipped so we never
        // leave a hole in the strip.
        var badges = new List<(Bitmap Bmp, bool On)>(6);
        if (_badgeAutoApprove != null) badges.Add((_badgeAutoApprove, menuOptionAutoApprove.Checked));
        if (_badgeFleet != null) badges.Add((_badgeFleet, menuOptionFleet.Checked));
        if (_badgeCaveman != null) badges.Add((_badgeCaveman, menuSessionCaveman.Checked));
        if (_badgeShowSteps != null) badges.Add((_badgeShowSteps, menuSessionShowSteps.Checked));
        if (_badgeLocalFilter != null) badges.Add((_badgeLocalFilter, menuOptionLocalFilter.Checked));
        if (_badgeToolSearch != null) badges.Add((_badgeToolSearch, menuOptionToolSearch.Checked));

        var fmt = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine;
        var labelSize = TextRenderer.MeasureText(g, label, font, Size.Empty, fmt);
        var chevronSize = TextRenderer.MeasureText(g, chevron, font, Size.Empty, fmt);

        int badgesWidth = badges.Count == 0
            ? 0
            : badges.Count * badgeSz + (badges.Count - 1) * badgeGap;
        int totalWidth = labelSize.Width + chevronSize.Width
                       + (badges.Count > 0 ? gap + badgesWidth + gap : gap);
        int x = (btn.Width - totalWidth) / 2;
        int y = (btn.Height - labelSize.Height) / 2;

        TextRenderer.DrawText(g, label, font, new Point(x, y), btn.ForeColor, fmt);
        x += labelSize.Width + gap;

        int badgeY = (btn.Height - badgeSz) / 2;
        foreach (var (bmp, on) in badges)
        {
            var dest = new Rectangle(x, badgeY, badgeSz, badgeSz);
            if (on || _disabledBadgeAttr == null)
            {
                g.DrawImage(bmp, dest);
            }
            else
            {
                g.DrawImage(
                    bmp,
                    dest,
                    0, 0, bmp.Width, bmp.Height,
                    GraphicsUnit.Pixel,
                    _disabledBadgeAttr);
            }
            x += badgeSz + badgeGap;
        }
        if (badges.Count > 0) x += gap - badgeGap;

        TextRenderer.DrawText(g, chevron, font, new Point(x, y), btn.ForeColor, fmt);
    }

    /// <summary>
    /// Edits the [SkillSources] URL list in gopilot.ini via
    /// <see cref="SkillSourcesDialog"/>. This list feeds the catalog browser;
    /// changing it does NOT trigger a session refresh on its own.
    /// </summary>
    private void EditSkillSources()
    {
        using var dialog = new SkillSourcesDialog(_settings.SkillSources);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.SkillSources = new List<string>(dialog.Urls);
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save settings to gopilot.ini:\n\n{ex.Message}",
                "Settings Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateSkillSourcesTooltip();
    }

    /// <summary>
    /// Manages the [McpServers] list in gopilot.ini via
    /// <see cref="McpServersDialog"/>. MCP servers are attached at session
    /// creation/resume, so when a change is made to a live session we defer a
    /// summary-and-restart handoff (the same mechanism used by Skill Tree and
    /// Fleet changes) so the next send picks up the new server set.
    /// </summary>
    /// <summary>
    /// Manages MCP servers via <see cref="McpServersDialog"/>, opened from
    /// Tools > MCP Servers. Always shows the dialog (manual entries plus servers
    /// discovered from .mcp.json across the search folders).
    /// </summary>
    private void EditMcpServers()
    {
        OpenMcpServersDialog(_copilot.DiscoverMcpServers(), scheduleHandoffIfConnected: true);
    }

    /// <summary>
    /// Shown automatically as a new session starts (before the README prompt and
    /// before the session is created) so the user can toggle off unwanted servers
    /// or add their own before anything loads. Only interrupts when at least one
    /// server was discovered from a .mcp.json file; otherwise it stays out of the
    /// way. Runs before session creation, so no handoff is needed.
    /// </summary>
    private void ReviewMcpServersBeforeSession()
    {
        var discovered = _copilot.DiscoverMcpServers();
        if (discovered.Count == 0) return;
        OpenMcpServersDialog(discovered, scheduleHandoffIfConnected: false);
    }

    /// <summary>
    /// Opens the MCP Servers manager with the supplied discovered set, persists
    /// any changes to gopilot.ini, and syncs them to the service. When
    /// <paramref name="scheduleHandoffIfConnected"/> is true and a session is
    /// live, a summary-and-restart handoff is scheduled so the change takes
    /// effect on the next send.
    /// </summary>
    private void OpenMcpServersDialog(List<McpServerEntry> discovered, bool scheduleHandoffIfConnected)
    {
        using var dialog = new McpServersDialog(
            _settings.McpServers, discovered, _settings.McpDisabledDiscovered);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.McpServers = dialog.Servers;
        _settings.McpDisabledDiscovered = dialog.DisabledDiscovered;
        _copilot.McpServers = dialog.Servers;
        _copilot.McpDisabledDiscovered = dialog.DisabledDiscovered;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save settings to gopilot.ini:\n\n{ex.Message}",
                "Settings Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (scheduleHandoffIfConnected && _copilot.IsConnected && _mainSessionId != null)
            ScheduleHandoff("MCP servers changed");
    }

    /// <summary>
    /// Opens the Skill Catalog browser. On dialog OK (at least one item
    /// installed), schedule a session handoff so the new tier-folder
    /// content participates in the next session creation.
    /// </summary>
    private void BrowseSkillCatalog()
    {
        using var dialog = new SkillCatalogBrowserDialog(_settings, _copilot);
        var result = dialog.ShowDialog(this);

        // Always refresh the SkillTree tooltip in case the dialog added a new tier.
        UpdateSkillTreeTooltip();
        UpdateSkillSourcesTooltip();

        if (result != DialogResult.OK) return;
        if (dialog.InstalledItems.Count == 0) return;

        var summary = new System.Text.StringBuilder();
        summary.Append($"Installed {dialog.InstalledItems.Count} item(s):");
        foreach (var r in dialog.InstalledItems)
            summary.Append("\r\n  ").Append(r.Kind).Append(" '").Append(r.Name).Append("' -> ").Append(r.TierLabel);
        summary.Append("\r\n\r\nRestart your session now to expose them?");

        var resp = MessageBox.Show(this, summary.ToString(),
            "Skill Catalog", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (resp == DialogResult.Yes && _copilot.IsConnected && _mainSessionId != null)
            ScheduleHandoff("Skill Catalog content downloaded");
    }

    private void ShowWorkspacePermissions()
    {
        using var dialog = new WorkspaceTrustDialog(
            _copilot.WorkingDirectory,
            preselectFolder: _copilot.WorkingDirectory);
        var result = dialog.ShowDialog(this);

        // Permissions-config.json is consulted by the CLI only at session
        // start.  When the user's saved changes touched the currently-active
        // workspace, schedule the same summary-and-restart handoff used by
        // Mode/Fleet/SkillTree changes so the new permission state actually
        // takes effect, and announce the new state in the output window so
        // the user can see what changed without re-opening the dialog.
        if (result == DialogResult.OK
            && dialog.AffectedActiveFolder
            && _copilot.IsConnected
            && _mainSessionId != null)
        {
            EmitPermissionsStatus("Workspace permissions edited");
            ScheduleHandoff("Workspace permissions changed");
        }
    }

    private void UpdateSkillSourcesTooltip()
    {
        var sources = _settings.SkillSources;
        string tip;
        if (sources.Count == 0)
        {
            tip = "Skill Sources: empty\n(click to add)";
        }
        else
        {
            var lines = new System.Text.StringBuilder();
            lines.Append($"Skill Sources ({sources.Count} URL{(sources.Count == 1 ? "" : "s")}):");
            foreach (var u in sources)
                lines.Append("\n  • ").Append(u);
            lines.Append("\n(click to edit)");
            tip = lines.ToString();
        }
        menuToolsSkillSources.ToolTipText = tip;
    }

    private void ShowAgentList()
    {
        // Make sure the cache reflects the current Skill Tree even before any
        // session has been created (or after an edit that has not yet
        // reconnected).
        _copilot.RebuildReferenceCache();

        var agents = _copilot.CachedAgents;
        if (agents.Count == 0)
        {
            MessageBox.Show(this,
                "No custom agents were found in the current Skill Tree, project, or personal (~/.copilot) folders.\r\n\r\n" +
                "Add an agents/*.md file under any tier folder to surface it here.",
                "No Agents",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = ReferenceListDialog.ForAgents(agents);
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedName))
            InsertNamedReferenceAtCursor("agent", dlg.SelectedName!);
    }

    private void ShowSkillList()
    {
        _copilot.RebuildReferenceCache();

        var skills = _copilot.CachedSkills;
        if (skills.Count == 0)
        {
            MessageBox.Show(this,
                "No skills (skills/*/SKILL.md) were found in the current Skill Tree, project, or personal (~/.copilot) folders.",
                "No Skills",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = ReferenceListDialog.ForSkills(skills);
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedName))
            InsertNamedReferenceAtCursor("skill", dlg.SelectedName!);
    }

    /// <summary>
    /// Opens the "List Prompts" picker dialog and, when the user accepts a row,
    /// attaches the underlying prompt file to the next outgoing message exactly
    /// the same way a file picked through "Add File..." is attached: a chip
    /// appears in the attachments strip, a relative-path @reference token is
    /// inserted at the caret, and the file's bytes are forwarded to Copilot as
    /// an Attachment when the user hits Send. There is no dedicated
    /// "@prompt:name" token because the SDK has no concept of a named prompt --
    /// prompts are user-curated content the model only sees once attached.
    /// </summary>
    private void ShowPromptList()
    {
        _copilot.RebuildReferenceCache();

        var prompts = _copilot.CachedPrompts;
        if (prompts.Count == 0)
        {
            MessageBox.Show(this,
                "No prompts (prompts/*.md) were found in the current Skill Tree, project, or personal (~/.copilot) folders.\r\n\r\n" +
                "Add a *.md file under any tier folder's prompts/ subdirectory to surface it here.",
                "No Prompts",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = ReferenceListDialog.ForPrompts(prompts);
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
            AddAttachment(dlg.SelectedPath!);
    }

    /// <summary>
    /// Inserts an "@kind:name" token (e.g. "@agent:doublecheck") at the prompt's
    /// caret position, mirroring the spacing rules used by file/folder attachments
    /// in <see cref="InsertReferenceAtCursor"/>.
    /// </summary>
    private void InsertNamedReferenceAtCursor(string kind, string name)
    {
        var reference = $"@{kind}:{name}";
        var pos = richTextBoxPrompt.SelectionStart;
        var text = richTextBoxPrompt.Text;

        if (pos > 0 && !char.IsWhiteSpace(text[pos - 1]))
            reference = " " + reference;

        reference += " ";

        richTextBoxPrompt.SelectionLength = 0;
        richTextBoxPrompt.SelectedText = reference;
        richTextBoxPrompt.Focus();
    }

    // ── Prompt history navigation ─────────────────────────────────────────────


    private void NavigateHistoryBack()
    {
        var text = _promptHistory.NavigateBack(richTextBoxPrompt.Text);
        richTextBoxPrompt.Text = text;
        richTextBoxPrompt.SelectionStart = text.Length;
        UpdateHistoryButtons();
    }

    private void NavigateHistoryForward()
    {
        var text = _promptHistory.NavigateForward();
        richTextBoxPrompt.Text = text;
        richTextBoxPrompt.SelectionStart = text.Length;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        buttonHistoryPrev.Enabled = _promptHistory.CanGoBack;
        buttonHistoryNext.Enabled = _promptHistory.CanGoForward;
    }

    private void RichTextBoxPrompt_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && e.Control)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _ = SendPromptAsync();
        }
    }

    // ── File / Folder attachment ──────────────────────────────────────────────

    private void ButtonAddFile_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Attach File(s) to Prompt",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            foreach (var path in dialog.FileNames)
                AddAttachment(path);
        }
    }

    private void ButtonAddFolder_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a Folder to Attach",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddAttachment(dialog.SelectedPath);
    }

    private static bool IsHiddenAttachment(string path) =>
        Path.GetFileName(path).Equals("gopilot-instructions.md", StringComparison.OrdinalIgnoreCase);

    private void AddAttachment(string path)
    {
        if (_attachments.Contains(path)) return;
        _attachments.Add(path);

        // Behind-the-scenes files are attached silently — no chip, no prompt reference.
        if (IsHiddenAttachment(path)) return;

        var chip = new Button
        {
            Text = Path.GetFileName(path) + "  ✕",
            AutoSize = true,
            BackColor = AppTheme.ButtonBg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = AppTheme.TextPrimary,
            Margin = new Padding(2, 2, 2, 2),
            Padding = new Padding(6, 2, 6, 2),
            Tag = path,
            Height = 24,
            UseVisualStyleBackColor = false,
        };
        chip.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
        chip.FlatAppearance.BorderSize = 1;
        toolTipMain.SetToolTip(chip, path);
        chip.Click += (_, _) => RemoveAttachment(path, chip);

        flowLayoutPanelChips.Controls.Add(chip);
        panelAttachments.Visible = true;

        InsertReferenceAtCursor(path);
    }

    private void InsertReferenceAtCursor(string path)
    {
        // Normalise directories (strip trailing separator so GetFileName works)
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Prefer a relative path from the working directory; fall back to just the name
        string reference;
        var root = _copilot.WorkingDirectory;
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            var rel = Path.GetRelativePath(root, path);
            // GetRelativePath returns the original path (or starts with ..) when outside root
            reference = rel.StartsWith("..") ? Path.GetFileName(path) : rel;
        }
        else
        {
            reference = Path.GetFileName(path);
        }

        if (string.IsNullOrEmpty(reference)) return;

        reference = $"@{reference}";
        var pos = richTextBoxPrompt.SelectionStart;
        var text = richTextBoxPrompt.Text;

        if (pos > 0 && !char.IsWhiteSpace(text[pos - 1]))
            reference = " " + reference;

        reference += " ";

        richTextBoxPrompt.SelectionLength = 0;
        richTextBoxPrompt.SelectedText = reference;
        richTextBoxPrompt.Focus();
    }

    /// <summary>
    /// Builds and attaches a right-click menu to the prompt editor that mirrors
    /// the most-used reference commands from the References menu (Add File,
    /// Add Folder, List Agents, List Skills) so they are reachable without
    /// leaving the keyboard/cursor focus. Standard clipboard verbs (Cut, Copy,
    /// Paste) are also exposed at the top of the menu.
    /// </summary>
    private void AttachPromptContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = AppTheme.StatusBar,
            ForeColor = AppTheme.TextPrimary,
            Renderer = new DarkMenuRenderer(),
        };

        ToolStripMenuItem Item(string text, EventHandler handler, Keys shortcut = Keys.None)
        {
            var item = new ToolStripMenuItem(text)
            {
                BackColor = AppTheme.StatusBar,
                ForeColor = AppTheme.TextPrimary,
            };
            if (shortcut != Keys.None)
            {
                item.ShortcutKeyDisplayString = ShortcutToText(shortcut);
            }
            item.Click += handler;
            return item;
        }

        var cutItem = Item("Cu&t", (_, _) => richTextBoxPrompt.Cut(), Keys.Control | Keys.X);
        var copyItem = Item("&Copy", (_, _) => richTextBoxPrompt.Copy(), Keys.Control | Keys.C);
        var pasteItem = Item("&Paste", (_, _) => richTextBoxPrompt.Paste(), Keys.Control | Keys.V);

        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("📄 Add &File...", ButtonAddFile_Click));
        menu.Items.Add(Item("📁 Add F&older...", ButtonAddFolder_Click));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("List &Agents...", (_, _) => ShowAgentList()));
        menu.Items.Add(Item("List &Skills...", (_, _) => ShowSkillList()));
        menu.Items.Add(Item("List Pro&mpts...", (_, _) => ShowPromptList()));

        menu.Opening += (_, _) =>
        {
            bool readOnly = richTextBoxPrompt.ReadOnly;
            bool hasSelection = richTextBoxPrompt.SelectionLength > 0;
            bool clipboardHas = ClipboardHasPastableContent();

            cutItem.Enabled = hasSelection && !readOnly;
            copyItem.Enabled = hasSelection;
            pasteItem.Enabled = clipboardHas && !readOnly;
        };

        richTextBoxPrompt.ContextMenuStrip = menu;
    }

    private static string ShortcutToText(Keys keys)
    {
        var parts = new List<string>();
        if ((keys & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((keys & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if ((keys & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        parts.Add((keys & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }

    private static bool ClipboardHasPastableContent()
    {
        try
        {
            return Clipboard.ContainsText()
                || Clipboard.ContainsImage()
                || Clipboard.ContainsFileDropList();
        }
        catch
        {
            return false;
        }
    }

    private void RemoveAttachment(string path, Control chip)
    {
        _attachments.Remove(path);
        flowLayoutPanelChips.Controls.Remove(chip);
        chip.Dispose();
        if (flowLayoutPanelChips.Controls.Count == 0)
            panelAttachments.Visible = false;
    }

    /// <summary>
    /// Scans the prompt RichTextBox's RTF for embedded pictures (pasted or
    /// dragged-in images), writes each one to "%TEMP%\GoPilot\clip-image-N.*",
    /// and returns the resulting file paths so the caller can forward them as
    /// attachments to Copilot. Raw PNG/JPEG payloads are written as-is;
    /// metafile/DIB payloads are re-encoded to PNG through GDI+.
    /// </summary>
    private static IReadOnlyList<string> ExtractEmbeddedImagesToTemp(string? rtf)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(rtf)) return results;

        var outDir = Path.Combine(Path.GetTempPath(), "GoPilot");
        Directory.CreateDirectory(outDir);

        int cursor = 0;
        while (true)
        {
            int start = rtf.IndexOf("{\\pict", cursor, StringComparison.Ordinal);
            if (start < 0) break;

            int end = FindRtfGroupEnd(rtf, start);
            if (end < 0) break;

            try
            {
                var (ext, bytes) = ParseRtfPictGroup(rtf, start, end);
                if (bytes is { Length: > 0 })
                {
                    var path = SaveImageBytes(outDir, ext, bytes);
                    if (path != null) results.Add(path);
                }
            }
            catch
            {
                // Swallow any single-image failure so one broken picture
                // doesn't block the rest of the send.
            }

            cursor = end + 1;
        }

        return results;
    }

    /// <summary>
    /// Walks the RTF starting at an opening brace and returns the index of the
    /// matching closing brace, honouring "\{" and "\}" escape sequences.
    /// Returns -1 if the group is unterminated.
    /// </summary>
    private static int FindRtfGroupEnd(string rtf, int open)
    {
        int depth = 0;
        for (int i = open; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '\\' && i + 1 < rtf.Length)
            {
                // Skip escaped brace; ordinary control words also consume the
                // next char but they never look like a brace, so this suffices.
                char next = rtf[i + 1];
                if (next == '{' || next == '}' || next == '\\') { i++; continue; }
            }
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Parses a "{\pict ...}" group and returns a preferred extension plus the
    /// decoded binary payload. Nested option sub-groups (e.g. "{\*\blipuid ...}")
    /// and control words are skipped so only the hex picture data is collected.
    /// </summary>
    private static (string ext, byte[]? bytes) ParseRtfPictGroup(string rtf, int start, int end)
    {
        string ext = ".bin";
        var hex = new System.Text.StringBuilder((end - start) / 2);

        int i = start + 1;
        while (i < end)
        {
            char c = rtf[i];

            if (c == '{')
            {
                int inner = FindRtfGroupEnd(rtf, i);
                if (inner < 0 || inner >= end) break;
                i = inner + 1;
                continue;
            }

            if (c == '\\')
            {
                // Read control word: "\<letters>[-]<digits>?" optionally
                // followed by a single delimiter space.
                int j = i + 1;
                if (j < end && (rtf[j] == '{' || rtf[j] == '}' || rtf[j] == '\\'))
                {
                    // Escaped literal; not a picture-type marker.
                    i = j + 1;
                    continue;
                }
                int wordStart = j;
                while (j < end && char.IsLetter(rtf[j])) j++;
                string word = rtf.Substring(wordStart, j - wordStart);

                if (word.Length > 0) ext = MatchPictExtension(word) ?? ext;

                if (j < end && (rtf[j] == '-' || char.IsDigit(rtf[j])))
                {
                    if (rtf[j] == '-') j++;
                    while (j < end && char.IsDigit(rtf[j])) j++;
                }
                if (j < end && rtf[j] == ' ') j++;
                i = j;
                continue;
            }

            if (IsHexDigit(c)) hex.Append(c);
            // Any other character (whitespace, newline, CR) is ignored.
            i++;
        }

        if (hex.Length < 2 || (hex.Length & 1) != 0) return (ext, null);
        var bytes = HexToBytes(hex.ToString());
        return (ext, bytes);
    }

    private static string? MatchPictExtension(string controlWord) => controlWord switch
    {
        "pngblip" => ".png",
        "jpegblip" => ".jpg",
        "emfblip" => ".emf",
        "wmetafile" or "wmetafile8" => ".wmf",
        "dibitmap" or "dibitmap0" => ".dib",
        "wbitmap" or "wbitmap0" => ".bmp",
        _ => null,
    };

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int k = 0; k < bytes.Length; k++)
            bytes[k] = (byte)((HexNibble(hex[k * 2]) << 4) | HexNibble(hex[k * 2 + 1]));
        return bytes;
    }

    private static int HexNibble(char c) =>
        c <= '9' ? c - '0' :
        c <= 'F' ? c - 'A' + 10 :
                   c - 'a' + 10;

    /// <summary>
    /// Writes the picture bytes to a unique "clip-image-N.*" file under the
    /// provided directory. PNG/JPEG payloads are written verbatim; DIB, WMF,
    /// EMF, and BMP payloads are re-encoded to PNG so the downstream model
    /// always receives a format it can read.
    /// </summary>
    private static string? SaveImageBytes(string dir, string ext, byte[] bytes)
    {
        int next = 1;
        foreach (var existing in Directory.EnumerateFiles(dir, "clip-image-*.*"))
        {
            var name = Path.GetFileNameWithoutExtension(existing);
            var dash = name.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(name.AsSpan(dash + 1), out var n) && n >= next)
                next = n + 1;
        }

        if (ext is ".png" or ".jpg")
        {
            var path = Path.Combine(dir, $"clip-image-{next}{ext}");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // DIB/WMF/EMF/BMP: transcode to PNG so the attachment is universally
        // consumable. If the round-trip fails, write the raw bytes with the
        // original extension as a best-effort fallback.
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            var pngPath = Path.Combine(dir, $"clip-image-{next}.png");
            img.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            return pngPath;
        }
        catch
        {
            var fallback = Path.Combine(dir, $"clip-image-{next}{ext}");
            File.WriteAllBytes(fallback, bytes);
            return fallback;
        }
    }

    // ── Session ───────────────────────────────────────────────────────────────

    private void OnSessionCreated(string sessionId, bool isSubAgent)
    {
        if (!isSubAgent)
        {
            _mainSessionId = sessionId;
            SaveSessionMetadata(sessionId);
        }
        else
        {
            // Track the sub-agent's session id so we can recognise its termination
            // via session.deleted even if the subagent.completed event is missing.
            _activeSubAgentSessions.Add(sessionId);
        }

        var label = isSubAgent ? "Sub-agent" : "Session";
        AppendOutput($"[{label} {sessionId[..Math.Min(8, sessionId.Length)]}… started]\r\n\r\n",
            AppTheme.ColorMeta);

        // Also push to the Rendered tab
        var sessionBlock = new OutputBlock(BlockKind.Status)
        {
            Content = $"[{label} {sessionId[..Math.Min(8, sessionId.Length)]}... started]",
            IsComplete = true
        };
        _outputBlocks.Add(sessionBlock);
        WebViewAppendBlock(sessionBlock);

        toolStripStatusLabelSession.Text =
            $"Session: {sessionId[..Math.Min(32, sessionId.Length)]}…";
    }

    // ── Output rendering ─────────────────────────────────────────────────────

    private void AppendMessage(SessionMessageEventArgs args)
    {
        // Dual-write: push to the WebView2 Rendered tab
        AppendRenderedMessage(args);

        bool scrollNeeded = false;

        switch (args.Kind)
        {
            case MessageKind.AssistantDelta:
                if (!_streamingSessions.Contains(args.SessionId))
                {
                    AppendRaw("🤖 Assistant:\r\n", AppTheme.ColorAssistant);
                    _streamingSessions.Add(args.SessionId);
                }
                AppendRaw(args.Content, AppTheme.ColorDefault);
                scrollNeeded = true;
                break;

            case MessageKind.AssistantFinal:
                if (!_streamingSessions.Contains(args.SessionId))
                    AppendRaw($"🤖 Assistant:\r\n{args.Content}\r\n\r\n", AppTheme.ColorAssistant);
                scrollNeeded = true;
                break;

            case MessageKind.Reasoning:
                AppendRaw($"💭 Reasoning:\r\n{args.Content}\r\n\r\n", AppTheme.ColorReasoning);
                scrollNeeded = true;
                break;

            case MessageKind.SubAgentStart:
                {
                    if (_streamingSessions.Remove(args.SessionId))
                        AppendRaw("\r\n", AppTheme.ColorDefault);
                    AppendRaw("\r\n", AppTheme.ColorDefault);
                    if (!string.IsNullOrEmpty(args.ToolCallId))
                    {
                        _activeSubAgents[args.ToolCallId] = args.SubAgentDisplayName ?? args.Content;
                        _subAgentStartPositions[args.ToolCallId] = richTextBoxOutput.TextLength;
                    }
                    var saName = args.SubAgentDisplayName ?? args.Content;
                    var saDesc = string.IsNullOrEmpty(args.SubAgentDescription) ? ""
                        : $" — {(args.SubAgentDescription.Length > 60 ? args.SubAgentDescription[..60] + "…" : args.SubAgentDescription)}";
                    AppendRaw($"○ {saName}{saDesc}\r\n", AppTheme.ColorSubAgent);
                    UpdateAgentStatus();
                    scrollNeeded = true;
                    break;
                }

            case MessageKind.SubAgentComplete:
                {
                    if (!string.IsNullOrEmpty(args.ToolCallId))
                    {
                        _activeSubAgents.Remove(args.ToolCallId);
                        _completedAgentCount++;
                        if (_subAgentStartPositions.TryGetValue(args.ToolCallId, out var saPos))
                        {
                            _subAgentStartPositions.Remove(args.ToolCallId);
                            WithoutRedraw(richTextBoxOutput, () =>
                            {
                                richTextBoxOutput.Select(saPos, 1);
                                richTextBoxOutput.SelectionColor = AppTheme.ColorAssistant;
                                richTextBoxOutput.SelectedText = "◉";
                                richTextBoxOutput.SelectionStart = richTextBoxOutput.TextLength;
                            });
                        }
                    }
                    var stats = FormatSubAgentStats(args);
                    if (stats != null)
                    {
                        AppendRaw($"  ↳ {stats}\r\n", AppTheme.ColorToolDim);
                        scrollNeeded = true;
                    }
                    if (_mainSessionIdle && _activeSubAgents.Count == 0)
                        CompleteMainSession();
                    else
                    {
                        if (_mainSessionIdle) RestartSubAgentWatchdog();
                        UpdateAgentStatus();
                    }
                    break;
                }

            case MessageKind.SubAgentFailed:
                {
                    if (!string.IsNullOrEmpty(args.ToolCallId))
                    {
                        _activeSubAgents.Remove(args.ToolCallId);
                        _completedAgentCount++;
                        if (_subAgentStartPositions.TryGetValue(args.ToolCallId, out var saPos))
                        {
                            _subAgentStartPositions.Remove(args.ToolCallId);
                            WithoutRedraw(richTextBoxOutput, () =>
                            {
                                richTextBoxOutput.Select(saPos, 1);
                                richTextBoxOutput.SelectionColor = AppTheme.ColorError;
                                richTextBoxOutput.SelectedText = "✗";
                                richTextBoxOutput.SelectionStart = richTextBoxOutput.TextLength;
                            });
                        }
                    }
                    if (!string.IsNullOrEmpty(args.Content))
                    {
                        AppendRaw($"  ✗ {args.SubAgentDisplayName}: {args.Content}\r\n", AppTheme.ColorError);
                        scrollNeeded = true;
                    }
                    var failStats = FormatSubAgentStats(args);
                    if (failStats != null)
                    {
                        AppendRaw($"  ↳ {failStats}\r\n", AppTheme.ColorToolDim);
                        scrollNeeded = true;
                    }
                    if (_mainSessionIdle && _activeSubAgents.Count == 0)
                        CompleteMainSession();
                    else
                    {
                        if (_mainSessionIdle) RestartSubAgentWatchdog();
                        UpdateAgentStatus();
                    }
                    break;
                }

            case MessageKind.SkillInvoked:
                {
                    if (_streamingSessions.Remove(args.SessionId))
                        AppendRaw("\r\n", AppTheme.ColorDefault);
                    var desc = string.IsNullOrEmpty(args.SubAgentDescription) ? ""
                        : $" — {args.SubAgentDescription}";
                    AppendRaw($"  📚 Skill: {args.Content}{desc}\r\n", AppTheme.ColorMeta);
                    scrollNeeded = true;
                    break;
                }

            case MessageKind.CustomAgentsUpdated:
                AppendRaw($"[{args.Content}]\r\n\r\n", AppTheme.ColorMeta);
                scrollNeeded = true;
                break;

            case MessageKind.ToolStart:
                {
                    if (_streamingSessions.Remove(args.SessionId))
                        AppendRaw("\r\n", AppTheme.ColorDefault);
                    var argPart = string.IsNullOrEmpty(args.ToolArgSummary) ? "" : $"  {args.ToolArgSummary}";
                    AppendRaw($"  🔧 {args.Content}{argPart}", AppTheme.ColorTool);
                    if (!string.IsNullOrEmpty(args.ToolCallId))
                        _toolStartPositions[args.ToolCallId] = richTextBoxOutput.TextLength;
                    scrollNeeded = true;
                    break;
                }

            case MessageKind.ToolProgress:
                if (!string.IsNullOrEmpty(args.Content))
                {
                    AppendRaw($"\r\n  │ {args.Content}", AppTheme.ColorToolDim);
                    scrollNeeded = true;
                }
                break;

            case MessageKind.ToolComplete:
                {
                    if (!string.IsNullOrEmpty(args.ToolCallId) &&
                        _toolStartPositions.TryGetValue(args.ToolCallId, out var insertAt))
                    {
                        _toolStartPositions.Remove(args.ToolCallId);
                        string tick = args.ToolSuccess ? " ✓\r\n" : " ✗\r\n";
                        WithoutRedraw(richTextBoxOutput, () =>
                        {
                            richTextBoxOutput.Select(insertAt, 0);
                            richTextBoxOutput.SelectionColor = args.ToolSuccess ? AppTheme.ColorAssistant : AppTheme.ColorError;
                            richTextBoxOutput.SelectedText = tick;
                            foreach (var key in _toolStartPositions.Keys.ToList())
                                if (_toolStartPositions[key] >= insertAt)
                                    _toolStartPositions[key] += tick.Length;
                            foreach (var key in _subAgentStartPositions.Keys.ToList())
                                if (_subAgentStartPositions[key] >= insertAt)
                                    _subAgentStartPositions[key] += tick.Length;
                            richTextBoxOutput.SelectionStart = richTextBoxOutput.TextLength;
                        });
                        if (!string.IsNullOrEmpty(args.ToolResultSummary))
                        {
                            var rc = args.ToolSuccess ? AppTheme.ColorToolDim : AppTheme.ColorError;
                            AppendRaw($"  └ {args.ToolResultSummary}\r\n", rc);
                            scrollNeeded = true;
                        }
                    }
                    else
                    {
                        AppendRaw(args.ToolSuccess ? "  ✓\r\n" : "  ✗\r\n",
                            args.ToolSuccess ? AppTheme.ColorAssistant : AppTheme.ColorError);
                        scrollNeeded = true;
                    }
                    break;
                }

            case MessageKind.BytesUpdate:
                _totalBytesReceived = args.TotalBytes;
                UpdateWorkingState();
                break; // no text appended — no scroll needed

            case MessageKind.Error:
                if (_streamingSessions.Remove(args.SessionId))
                    AppendRaw("\r\n", AppTheme.ColorDefault);
                AppendRaw($"\r\n❌ Error: {args.Content}\r\n\r\n", AppTheme.ColorError);
                if (args.Content.Contains("CAPIError: 400") || args.Content.Contains("400 Bad Request"))
                    AppendRaw(
                        "💡 Tip: This usually means the session's context window is full. " +
                        "Try changing the mode or model to start a fresh session.\r\n\r\n",
                        AppTheme.ColorMeta);
                scrollNeeded = true;
                break;

            case MessageKind.Status:
                AppendRaw($"[{args.Content}]\r\n", AppTheme.ColorMeta);
                scrollNeeded = true;
                break;
        }

        if (scrollNeeded)
            richTextBoxOutput.ScrollToCaret();
    }

    private void OnSessionIdle(string sessionId)
    {
        // Phase 4.5: idle is the authoritative trailing-case signal -- close any
        // open Reasoning/Tool sections for this session so they collapse with
        // a smart summary instead of being left visually mid-stream.
        CloseAnyOpenSectionsForSession(sessionId);

        if (_streamingSessions.Remove(sessionId))
            AppendOutput("\r\n\r\n", AppTheme.ColorDefault);

        if (sessionId == _mainSessionId)
        {
            if (_activeSubAgents.Count > 0)
            {
                // If the SDK reports no live sub-agent sessions, the orchestrator
                // really is finished — any lingering _activeSubAgents entries are
                // stale (a subagent.completed event was lost). Don't keep the user
                // staring at "Working..." forever; force completion.
                if (_activeSubAgentSessions.Count == 0)
                {
                    ForceCompleteStaleSubAgents("main session idle with no live sub-agent sessions");
                    return;
                }

                // Orchestrator finished dispatching but Fleet sub-agents are still running.
                // Defer completion until the last sub-agent fires complete/failed,
                // and arm the watchdog so we recover if that event never arrives.
                _mainSessionIdle = true;
                RestartSubAgentWatchdog();
                UpdateAgentStatus();
            }
            else
            {
                CompleteMainSession();
            }
        }
    }

    /// <summary>
    /// Called when the SDK reports a non-main session was deleted. This is the
    /// most reliable signal that a sub-agent has truly ended, even if its paired
    /// subagent.completed/failed event was dropped.
    /// </summary>
    private void OnSubAgentSessionEnded(string sessionId)
    {
        if (!_activeSubAgentSessions.Remove(sessionId))
            return;

        _streamingSessions.Remove(sessionId);

        // If the main session has already gone idle and no live sub-agent sessions
        // remain, the SDK is truly done. Sweep any stale _activeSubAgents entries
        // and complete; rearm the watchdog only if work is still genuinely pending.
        if (_mainSessionIdle && _activeSubAgentSessions.Count == 0)
        {
            ForceCompleteStaleSubAgents("all sub-agent sessions ended");
        }
        else if (_mainSessionIdle)
        {
            RestartSubAgentWatchdog();
            UpdateAgentStatus();
        }
        else
        {
            UpdateAgentStatus();
        }
    }

    /// <summary>
    /// Recover from a stuck "Working..." state by clearing any leaked sub-agent
    /// tracking entries and completing the main session. Logs a meta line to the
    /// transcript so the discrepancy is visible.
    /// </summary>
    private void ForceCompleteStaleSubAgents(string reason)
    {
        if (_activeSubAgents.Count > 0)
        {
            var names = string.Join(", ", _activeSubAgents.Values);
            AppendOutput(
                $"[Cleared {_activeSubAgents.Count} stale sub-agent entr"
                    + (_activeSubAgents.Count == 1 ? "y" : "ies")
                    + $" ({names}) — {reason}]\r\n",
                AppTheme.ColorMeta);
            _activeSubAgents.Clear();
        }
        CompleteMainSession();
    }

    private void RestartSubAgentWatchdog()
    {
        _subAgentWatchdog.Stop();
        _subAgentWatchdog.Start();
    }

    /// <summary>
    /// Force-reset all per-prompt tracking state. Used by reset/reconnect paths
    /// that bypass the normal CompleteMainSession flow so no stale "Working..."
    /// or sub-agent indicators leak across session boundaries.
    /// </summary>
    private void UpdateTitleBar(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            this.Text = "GoPilot";
        }
        else
        {
            this.Text = $"GoPilot : {folder}";
        }
    }

    private void ResetSessionTrackingState()
    {
        _subAgentWatchdog.Stop();
        _pendingCount = 0;
        _mainSessionIdle = false;
        _totalBytesReceived = 0;
        _completedAgentCount = 0;
        _activeSubAgents.Clear();
        _activeSubAgentSessions.Clear();
        _streamingSessions.Clear();
        _toolStartPositions.Clear();
        _subAgentStartPositions.Clear();
        ClearSectionTrackers();
    }

    private void CompleteMainSession()
    {
        _subAgentWatchdog.Stop();
        // Only chime when real work was in flight — back-to-back idle events
        // (e.g. coalesced Dispatch calls) can re-enter this method with a
        // pending count of zero and would otherwise produce a phantom chime.
        bool hadPendingWork = _pendingCount > 0;
        _mainSessionIdle = false;
        // session.idle from the SDK means the main session has nothing left to
        // process. Multiple back-to-back Dispatch calls (e.g. README + backup
        // load on New Session) can be coalesced into a single SDK turn, so we
        // must reset to 0 here rather than decrement — otherwise the UI stays
        // stuck on "Working..." with leftover phantom pending counts.
        _pendingCount = 0;
        _totalBytesReceived = 0;
        _activeSubAgents.Clear();
        _activeSubAgentSessions.Clear();
        _streamingSessions.Clear();
        _toolStartPositions.Clear();
        _subAgentStartPositions.Clear();
        _completedAgentCount = 0;
        UpdateWorkingState();
        if (hadPendingWork) SoundService.PlayWorkComplete();
    }

    private static string? FormatSubAgentStats(SessionMessageEventArgs args)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(args.SubAgentModel))
            parts.Add(args.SubAgentModel);
        if (args.SubAgentTotalCalls is > 0)
            parts.Add($"{(int)args.SubAgentTotalCalls} call{((int)args.SubAgentTotalCalls == 1 ? "" : "s")}");
        if (args.SubAgentTotalTokens is > 0)
        {
            var t = args.SubAgentTotalTokens.Value;
            parts.Add(t >= 1000 ? $"{t / 1000:F1}K tokens" : $"{(int)t} tokens");
        }
        if (args.SubAgentDurationMs is > 0)
            parts.Add($"{args.SubAgentDurationMs.Value / 1000:F1}s");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    // ── Rendered output (WebView2) ──────────────────────────────────────────

    // Maps sessionId → current streaming OutputBlock for that session
    private readonly Dictionary<string, OutputBlock> _streamingBlocks = new();

    private void AppendRenderedMessage(SessionMessageEventArgs args)
    {
        if (!_webViewReady) return;

        // Phase 4.5: any session message means the model is responding -- if a
        // "Thinking..." pill is up, take it down before painting the first event.
        // BytesUpdate fires from network plumbing without UI implication, so don't
        // count it as the first real event of the turn.
        if (_activeThinkingId != null && args.Kind != MessageKind.BytesUpdate)
        {
            WebViewRemoveThinking(_activeThinkingId);
            _activeThinkingId = null;
        }

        switch (args.Kind)
        {
            case MessageKind.AssistantDelta:
                {
                    // Assistant text begins -- close any open Reasoning/Tool sections
                    // for this session so they get a smart summary BEFORE the reply
                    // streams in.  Errors must always be surfaced top-level too.
                    CloseAnyOpenSectionsForSession(args.SessionId);

                    if (!_streamingBlocks.TryGetValue(args.SessionId, out var block))
                    {
                        block = new OutputBlock(BlockKind.Assistant) { Label = "\U0001f916 Assistant:" };
                        _outputBlocks.Add(block);
                        _streamingBlocks[args.SessionId] = block;
                        WebViewAppendBlock(block);
                    }
                    block.Content += args.Content;
                    WebViewUpdateBlock(block);
                    break;
                }

            case MessageKind.AssistantFinal:
                {
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    if (_streamingBlocks.TryGetValue(args.SessionId, out var block))
                    {
                        block.IsComplete = true;
                        _streamingBlocks.Remove(args.SessionId);
                        WebViewFinalizeBlock(block);
                    }
                    else
                    {
                        // Non-streamed final message
                        var finalBlock = new OutputBlock(BlockKind.Assistant)
                        {
                            Label = "\U0001f916 Assistant:",
                            Content = args.Content,
                            IsComplete = true
                        };
                        _outputBlocks.Add(finalBlock);
                        WebViewAppendBlock(finalBlock);
                        WebViewFinalizeBlock(finalBlock);
                    }
                    break;
                }

            case MessageKind.Reasoning:
                {
                    // Reasoning closes any open Tool section first (different kind).
                    CloseToolSection(args.SessionId);

                    if (!_openReasoningSections.TryGetValue(args.SessionId, out var sec))
                    {
                        sec = new OpenReasoningSection
                        {
                            SectionId = $"r{System.Threading.Interlocked.Increment(ref _nextSectionId)}",
                            SessionId = args.SessionId,
                            StartedAtUtc = DateTime.UtcNow,
                        };
                        _openReasoningSections[args.SessionId] = sec;
                        WebViewAppendSection(sec.SectionId, "reasoning", "\U0001f4ad Reasoning...");
                    }
                    sec.Chunks++;
                    if (sec.Content.Length > 0)
                        sec.Content.Append("\r\n\r\n");
                    sec.Content.Append(args.Content);
                    // Pass raw markdown to JS; the renderer parses it inside setSectionContent.
                    WebViewSetSectionContent(sec.SectionId, sec.Content.ToString(), isMarkdown: true);
                    break;
                }

            case MessageKind.SubAgentStart:
                {
                    FinalizeStreamingBlock(args.SessionId);
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    var saName = args.SubAgentDisplayName ?? args.Content;
                    var saDesc = string.IsNullOrEmpty(args.SubAgentDescription) ? ""
                        : $" -- {(args.SubAgentDescription.Length > 60 ? args.SubAgentDescription[..60] + "..." : args.SubAgentDescription)}";
                    var block = new OutputBlock(BlockKind.SubAgent)
                    {
                        Content = $"\u25CB {saName}{saDesc}"
                    };
                    _outputBlocks.Add(block);
                    if (!string.IsNullOrEmpty(args.ToolCallId))
                        _renderedSubAgentBlocks[args.ToolCallId] = block;
                    WebViewAppendBlock(block);
                    break;
                }

            case MessageKind.SubAgentComplete:
                {
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    if (!string.IsNullOrEmpty(args.ToolCallId) &&
                        _renderedSubAgentBlocks.TryGetValue(args.ToolCallId, out var saBlock))
                    {
                        _renderedSubAgentBlocks.Remove(args.ToolCallId);
                        var stats = FormatSubAgentStats(args);
                        if (stats != null)
                            WebViewAppendToolStatus(saBlock,
                                $"<span class=\"subagent-complete\">\u25C9</span> {EscapeForJs(stats)}");
                    }
                    break;
                }

            case MessageKind.SubAgentFailed:
                {
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    if (!string.IsNullOrEmpty(args.ToolCallId) &&
                        _renderedSubAgentBlocks.TryGetValue(args.ToolCallId, out var saBlock))
                    {
                        _renderedSubAgentBlocks.Remove(args.ToolCallId);
                        var msg = !string.IsNullOrEmpty(args.Content)
                            ? $"<span class=\"subagent-failed\">\u2717 {EscapeForJs(args.SubAgentDisplayName ?? "")}: {EscapeForJs(args.Content)}</span>"
                            : "<span class=\"subagent-failed\">\u2717</span>";
                        WebViewAppendToolStatus(saBlock, msg);
                    }
                    break;
                }

            case MessageKind.SkillInvoked:
                {
                    FinalizeStreamingBlock(args.SessionId);
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    var desc = string.IsNullOrEmpty(args.SubAgentDescription) ? ""
                        : $" -- {args.SubAgentDescription}";
                    var block = new OutputBlock(BlockKind.Status)
                    {
                        Content = $"\U0001f4da Skill: {args.Content}{desc}",
                        IsComplete = true
                    };
                    _outputBlocks.Add(block);
                    WebViewAppendBlock(block);
                    break;
                }

            case MessageKind.CustomAgentsUpdated:
                {
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    var block = new OutputBlock(BlockKind.Status)
                    {
                        Content = $"[{args.Content}]",
                        IsComplete = true
                    };
                    _outputBlocks.Add(block);
                    WebViewAppendBlock(block);
                    break;
                }

            case MessageKind.ToolStart:
                {
                    // Tools close any open Reasoning section first (different kind).
                    FinalizeStreamingBlock(args.SessionId);
                    CloseReasoningSection(args.SessionId);

                    if (!_openToolSections.TryGetValue(args.SessionId, out var sec))
                    {
                        sec = new OpenToolSection
                        {
                            SectionId = $"t{System.Threading.Interlocked.Increment(ref _nextSectionId)}",
                            SessionId = args.SessionId,
                            StartedAtUtc = DateTime.UtcNow,
                        };
                        _openToolSections[args.SessionId] = sec;
                        WebViewAppendSection(sec.SectionId, "tools", "\U0001f527 Working...");
                    }
                    var lineId = !string.IsNullOrEmpty(args.ToolCallId)
                        ? args.ToolCallId
                        : $"l{System.Threading.Interlocked.Increment(ref _nextSectionId)}";
                    var line = new OpenToolLine
                    {
                        LineId = lineId,
                        ToolName = args.Content ?? "",
                        ArgSummary = args.ToolArgSummary ?? "",
                    };
                    sec.Lines.Add(line);
                    _openToolLines[lineId] = line;
                    WebViewAppendSectionLine(sec.SectionId, lineId, RebuildToolLineHtml(line));
                    break;
                }

            case MessageKind.ToolProgress:
                {
                    if (!string.IsNullOrEmpty(args.Content) &&
                        !string.IsNullOrEmpty(args.ToolCallId) &&
                        _openToolLines.TryGetValue(args.ToolCallId, out var line))
                    {
                        line.ProgressMessages.Add(args.Content);
                        WebViewUpdateSectionLine(line.LineId, RebuildToolLineHtml(line));
                    }
                    break;
                }

            case MessageKind.ToolComplete:
                {
                    if (!string.IsNullOrEmpty(args.ToolCallId) &&
                        _openToolLines.TryGetValue(args.ToolCallId, out var line))
                    {
                        line.Success = args.ToolSuccess;
                        line.ResultSummary = args.ToolResultSummary;
                        var section = FindToolSectionContaining(line);
                        if (section != null && !args.ToolSuccess)
                            section.HasFailure = true;
                        WebViewUpdateSectionLine(line.LineId, RebuildToolLineHtml(line));
                        if (!args.ToolSuccess)
                            WebViewMarkSectionLineFailed(line.LineId);
                    }
                    break;
                }

            case MessageKind.Error:
                {
                    // Errors are NEVER inside a collapsible. Close any open sections
                    // for this session cleanly first, then surface the error top-level.
                    FinalizeStreamingBlock(args.SessionId);
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    var block = new OutputBlock(BlockKind.Error)
                    {
                        Content = $"\u274C Error: {args.Content}",
                        IsComplete = true
                    };
                    _outputBlocks.Add(block);
                    WebViewAppendBlock(block);

                    if (args.Content.Contains("CAPIError: 400") || args.Content.Contains("400 Bad Request"))
                    {
                        var tipBlock = new OutputBlock(BlockKind.Status)
                        {
                            Content = "\U0001f4a1 Tip: This usually means the session's context window is full. " +
                                "Try changing the mode or model to start a fresh session.",
                            IsComplete = true
                        };
                        _outputBlocks.Add(tipBlock);
                        WebViewAppendBlock(tipBlock);
                    }
                    break;
                }

            case MessageKind.Status:
                {
                    CloseAnyOpenSectionsForSession(args.SessionId);
                    var block = new OutputBlock(BlockKind.Status)
                    {
                        Content = $"[{args.Content}]",
                        IsComplete = true
                    };
                    _outputBlocks.Add(block);
                    WebViewAppendBlock(block);
                    break;
                }
        }
    }

    // Maps toolCallId -> rendered block for sub-agent tracking
    private readonly Dictionary<string, OutputBlock> _renderedSubAgentBlocks = new();

    private void FinalizeStreamingBlock(string sessionId)
    {
        if (_streamingBlocks.TryGetValue(sessionId, out var block))
        {
            block.IsComplete = true;
            _streamingBlocks.Remove(sessionId);
            WebViewFinalizeBlock(block);
        }
    }

    // ── WebView2 JS bridge helpers ───────────────────────────────────────────

    private void WebViewAppendBlock(OutputBlock block)
    {
        // A structured block is being appended (User, Assistant, Tool, etc.).
        // Close any rolling meta block so subsequent AppendOutput calls start a new
        // Status block below it instead of being merged into the previous one.
        _currentMetaBlock = null;
        WebViewAppendBlockInternal(block);
    }

    /// <summary>
    /// Injects one or more inline image thumbnails into a previously-appended
    /// block in the Rendered tab. Used to echo pasted clipboard images
    /// underneath the user's prompt block. The block must already exist in
    /// the WebView (the JS bridge looks it up by id).
    ///
    /// Each item is a (src, alt) pair where <c>src</c> is a fully-formed
    /// <c>data:image/...;base64,...</c> URI built by
    /// <see cref="ImageReferenceTransformer.TryEncodeDataUri"/>. The bytes
    /// are inlined client-side only -- nothing is sent over the wire.
    /// </summary>
    private void WebViewAppendBlockImages(
        string blockId,
        IReadOnlyList<(string Src, string Alt)> items)
    {
        if (!_webViewReady || items.Count == 0) return;

        // Anonymous-typed payload serialises to {"src":..., "alt":..., "title":...}
        // which matches the JS appendBlockImages contract. JsonSerializer escapes
        // <, >, & by default so this is safe to embed in a <script> context.
        var payload = items
            .Select(t => new { src = t.Src, alt = t.Alt, title = t.Alt })
            .ToArray();
        var json = JsonSerializer.Serialize(payload);

        var js = $"appendBlockImages({JsString(blockId)}, {json})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    /// <summary>
    /// Encodes each image in <paramref name="attachmentPaths"/> as a base64
    /// data URI and appends a thumbnail beneath <paramref name="userBlock"/>
    /// in the Rendered tab. Mirrors a concise <c>[image: name.png]</c>
    /// marker per image into the Raw tab so the plain transcript records
    /// what was attached without bloating with base64 payloads.
    ///
    /// Non-image paths are skipped silently. Files that exceed
    /// <see cref="ImageReferenceTransformer.MaxFileBytes"/> or that fail
    /// to read are also skipped (the picture still goes to the SDK; only
    /// the local thumbnail is omitted).
    /// </summary>
    private void AppendPastedImagePreviews(
        OutputBlock userBlock,
        IReadOnlyList<string>? attachmentPaths)
    {
        if (attachmentPaths is null || attachmentPaths.Count == 0) return;

        var thumbs = new List<(string Src, string Alt)>();
        foreach (var path in attachmentPaths)
        {
            if (!IsImageExtension(path)) continue;

            var dataUri = ImageReferenceTransformer.TryEncodeDataUri(path);
            if (dataUri == null) continue;

            var alt = Path.GetFileName(path);
            thumbs.Add((dataUri, alt));
            AppendRaw($"[image: {alt}]\r\n", AppTheme.ColorUser);
        }

        if (thumbs.Count == 0) return;

        AppendRaw("\r\n", AppTheme.ColorUser);
        WebViewAppendBlockImages(userBlock.Id, thumbs);
    }

    private static bool IsImageExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif"
                   or ".webp" or ".bmp" or ".svg"
                   or ".ico" or ".tif" or ".tiff" or ".avif";
    }

    private void WebViewAppendBlockInternal(OutputBlock block)
    {
        if (!_webViewReady) return;
        var content = TransformBlockContentForRender(block, block.Content);
        var js = $"appendBlock({JsString(block.Id)}, {JsString(block.CssKind)}, " +
                 $"{JsString(block.Label)}, {JsString(content)}, {(block.IsMarkdown ? "true" : "false")})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewUpdateBlock(OutputBlock block)
    {
        if (!_webViewReady) return;
        var content = TransformBlockContentForRender(block, block.Content);
        var js = $"updateBlock({JsString(block.Id)}, {JsString(content)})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewFinalizeBlock(OutputBlock block)
    {
        if (!_webViewReady) return;
        var content = TransformBlockContentForRender(block, block.Content);
        // Flush content one last time, then finalize
        var js = $"updateBlock({JsString(block.Id)}, {JsString(content)}); " +
                 $"finalizeBlock({JsString(block.Id)})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewAppendToolStatus(OutputBlock block, string html)
    {
        if (!_webViewReady) return;
        var js = $"appendToolStatus({JsString(block.Id)}, {JsString(html)})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewClearAll()
    {
        _currentMetaBlock = null;
        if (!_webViewReady) return;
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync("clearAll()");
    }

    // ── Phase 4.5: Collapsible sections (Reasoning / Tool group) ─────────────

    /// <summary>Tracks an in-flight Reasoning section grouping consecutive
    /// AssistantReasoningEvents from a single session.</summary>
    private sealed class OpenReasoningSection
    {
        public string SectionId = "";
        public string SessionId = "";
        public DateTime StartedAtUtc;
        public int Chunks;
        public System.Text.StringBuilder Content = new();
    }

    /// <summary>Tracks an in-flight Tool group section grouping consecutive
    /// tool-call events from a single session.</summary>
    private sealed class OpenToolSection
    {
        public string SectionId = "";
        public string SessionId = "";
        public DateTime StartedAtUtc;
        public List<OpenToolLine> Lines = new();
        public bool HasFailure;
    }

    /// <summary>Per-tool-call line state inside an OpenToolSection.</summary>
    private sealed class OpenToolLine
    {
        public string LineId = "";
        public string ToolName = "";
        public string ArgSummary = "";
        public List<string> ProgressMessages = new();
        public bool? Success;       // null = still running
        public string? ResultSummary;
    }

    private OpenToolSection? FindToolSectionContaining(OpenToolLine line)
    {
        foreach (var sec in _openToolSections.Values)
            if (sec.Lines.Contains(line)) return sec;
        return null;
    }

    /// <summary>Closes any open Reasoning and Tool sections for the given
    /// session id, replacing each section's summary with a smart one-liner.
    /// Called by every non-grouped event handler so transitions are clean.</summary>
    private void CloseAnyOpenSectionsForSession(string sessionId)
    {
        CloseReasoningSection(sessionId);
        CloseToolSection(sessionId);
    }

    private void CloseReasoningSection(string sessionId)
    {
        if (!_openReasoningSections.TryGetValue(sessionId, out var sec)) return;
        _openReasoningSections.Remove(sessionId);
        var summary = SummariseReasoningSection(sec);
        var collapse = !_settings.DetailsDefaultOpen;
        WebViewCloseSection(sec.SectionId, summary, collapse, hasFailure: false);
    }

    private void CloseToolSection(string sessionId)
    {
        if (!_openToolSections.TryGetValue(sessionId, out var sec)) return;
        _openToolSections.Remove(sessionId);
        // Drop tool-line tracking for any in-flight or completed lines belonging
        // to this section. The DOM lines themselves remain visible inside the
        // collapsible until the user manually re-opens the section.
        foreach (var line in sec.Lines)
            _openToolLines.Remove(line.LineId);
        var summary = SummariseToolSection(sec);
        var collapse = !_settings.DetailsDefaultOpen;
        WebViewCloseSection(sec.SectionId, summary, collapse, sec.HasFailure);
    }

    private void ClearSectionTrackers()
    {
        // Politely close any sections still open in the DOM before dropping
        // C# tracking. This handles disconnect/reset paths that keep the
        // existing transcript visible -- without this, orphaned sections
        // would keep their streaming-pulse animation forever.
        foreach (var sec in _openReasoningSections.Values)
            WebViewCloseSection(sec.SectionId, "\U0001f4ad Reasoning (interrupted)",
                collapse: !_settings.DetailsDefaultOpen, hasFailure: false);
        foreach (var sec in _openToolSections.Values)
            WebViewCloseSection(sec.SectionId, "\U0001f527 Working... (interrupted)",
                collapse: !_settings.DetailsDefaultOpen, hasFailure: sec.HasFailure);

        _openReasoningSections.Clear();
        _openToolSections.Clear();
        _openToolLines.Clear();
        if (_activeThinkingId != null)
        {
            WebViewRemoveThinking(_activeThinkingId);
            _activeThinkingId = null;
        }
    }

    /// <summary>Builds the inner HTML of a single tool line, suitable for
    /// passing to appendSectionLine / updateSectionLine. All user-supplied
    /// strings are HTML-escaped via <see cref="EscapeForJs"/>.</summary>
    private static string RebuildToolLineHtml(OpenToolLine line)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("\U0001f527 ").Append(EscapeForJs(line.ToolName ?? ""));
        if (!string.IsNullOrEmpty(line.ArgSummary))
            sb.Append("  ").Append(EscapeForJs(line.ArgSummary));
        if (line.Success.HasValue)
        {
            var cls = line.Success.Value ? "tool-success" : "tool-failure";
            var tick = line.Success.Value ? "\u2713" : "\u2717";
            sb.Append(" <span class=\"").Append(cls).Append("\">").Append(tick).Append("</span>");
        }
        foreach (var msg in line.ProgressMessages)
        {
            sb.Append("<br/><span class=\"tool-dim\">\u2502 ")
              .Append(EscapeForJs(msg))
              .Append("</span>");
        }
        if (!string.IsNullOrEmpty(line.ResultSummary))
        {
            var cls = (line.Success == false) ? "tool-failure" : "tool-dim";
            sb.Append("<br/><span class=\"").Append(cls).Append("\">\u2514 ")
              .Append(EscapeForJs(line.ResultSummary))
              .Append("</span>");
        }
        return sb.ToString();
    }

    /// <summary>Generates the closed-state summary for a Reasoning section
    /// (e.g. "Reasoning (12s, 145 words)" or "Thought 3 times (4s, 245 words)").</summary>
    private static string SummariseReasoningSection(OpenReasoningSection sec)
    {
        var content = sec.Content.ToString();
        int words = content.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries).Length;
        var dur = (int)Math.Round((DateTime.UtcNow - sec.StartedAtUtc).TotalSeconds);

        string label = sec.Chunks <= 1
            ? "Reasoning"
            : (sec.Chunks == 2 ? "Thought twice" : $"Thought {sec.Chunks} times");

        var statBits = new List<string>();
        if (dur >= 1) statBits.Add($"{dur}s");
        if (words > 0) statBits.Add($"{words} word{(words == 1 ? "" : "s")}");
        var stats = statBits.Count > 0 ? $" ({string.Join(", ", statBits)})" : "";
        return $"\U0001f4ad {label}{stats}";
    }

    /// <summary>Generates the closed-state summary for a Tool group section
    /// using actual tool-name categories (e.g. "Read 3 files, edited 1 file (8s)").
    /// Failures are appended as a suffix in red on the JS side.</summary>
    private static string SummariseToolSection(OpenToolSection sec)
    {
        if (sec.Lines.Count == 0) return "\U0001f527 No tool calls";

        // Preserve the order tools first appeared in by iterating the list.
        var orderedCategories = new List<string>();
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int failed = 0;
        foreach (var line in sec.Lines)
        {
            if (line.Success == false) failed++;
            var cat = CategoriseTool(line.ToolName);
            if (!categoryCounts.ContainsKey(cat))
            {
                categoryCounts[cat] = 0;
                orderedCategories.Add(cat);
            }
            categoryCounts[cat]++;
        }

        var parts = new List<string>();
        foreach (var cat in orderedCategories)
            parts.Add(FormatCategory(cat, categoryCounts[cat]));

        var dur = (int)Math.Round((DateTime.UtcNow - sec.StartedAtUtc).TotalSeconds);
        var sumDur = dur >= 1 ? $" ({dur}s)" : "";
        var sumFail = failed > 0 ? $" -- {failed} failed" : "";
        return $"\U0001f527 {string.Join(", ", parts)}{sumDur}{sumFail}";
    }

    private static string CategoriseTool(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        // Order matters: more-specific verbs before generic substrings.
        if (n.Contains("apply_patch") || n.Contains("str_replace")
            || n.Contains("write") || n.Contains("edit")
            || n.Contains("create") || n.Contains("update")) return "edit";
        if (n.Contains("delete") || n.Contains("remove") || n.Contains("rm_")) return "delete";
        if (n.Contains("read") || n.Contains("view") || n.Contains("cat_")
            || n.Contains("get_content")) return "read";
        if (n.Contains("shell") || n.Contains("bash")
            || n.Contains("powershell") || n.Contains("terminal")
            || n.Contains("execute") || n.Contains("run_in")
            || n.Contains("exec_") || n.Contains("invoke")) return "run";
        if (n.Contains("search") || n.Contains("grep")
            || n.Contains("find") || n.Contains("glob")) return "search";
        if (n.Contains("fetch") || n.Contains("http") || n.Contains("url")
            || n.Contains("download")) return "fetch";
        if (n.Contains("ask_user") || n.Contains("input")) return "ask";
        return "other";
    }

    private static string FormatCategory(string cat, int n) => cat switch
    {
        "edit" => $"Edited {n} file{(n == 1 ? "" : "s")}",
        "delete" => $"Deleted {n} file{(n == 1 ? "" : "s")}",
        "read" => $"Read {n} file{(n == 1 ? "" : "s")}",
        "run" => $"Ran {n} command{(n == 1 ? "" : "s")}",
        "search" => $"Searched {n} time{(n == 1 ? "" : "s")}",
        "fetch" => $"Fetched {n} URL{(n == 1 ? "" : "s")}",
        "ask" => $"Asked {n} question{(n == 1 ? "" : "s")}",
        _ => $"Used {n} tool{(n == 1 ? "" : "s")}",
    };

    // ── WebView2 JS bridge: section + thinking helpers ───────────────────────

    private void WebViewAppendSection(string sectionId, string sectionKind, string summaryText)
    {
        if (!_webViewReady) return;
        // Sections always start open while streaming so the user can watch
        // content arrive. The collapse decision happens at close time and is
        // governed by the menu setting "Show Working Steps".
        var js = $"appendSection({JsString(sectionId)}, {JsString(sectionKind)}, " +
                 $"{JsString(summaryText)}, true)";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewSetSectionContent(string sectionId, string content, bool isMarkdown)
    {
        if (!_webViewReady) return;
        var js = $"setSectionContent({JsString(sectionId)}, {JsString(content)}, " +
                 $"{(isMarkdown ? "true" : "false")})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewAppendSectionLine(string sectionId, string lineId, string html)
    {
        if (!_webViewReady) return;
        var js = $"appendSectionLine({JsString(sectionId)}, {JsString(lineId)}, {JsString(html)})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewUpdateSectionLine(string lineId, string html)
    {
        if (!_webViewReady) return;
        var js = $"updateSectionLine({JsString(lineId)}, {JsString(html)})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewMarkSectionLineFailed(string lineId)
    {
        if (!_webViewReady) return;
        var js = $"markSectionLineFailed({JsString(lineId)})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewCloseSection(string sectionId, string summary, bool collapse, bool hasFailure)
    {
        if (!_webViewReady) return;
        var js = $"closeSection({JsString(sectionId)}, {JsString(summary)}, " +
                 $"{(collapse ? "true" : "false")}, {(hasFailure ? "true" : "false")})";
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync(js);
    }

    private void WebViewAppendThinking(string id)
    {
        if (!_webViewReady) return;
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync($"appendThinking({JsString(id)})");
    }

    private void WebViewRemoveThinking(string id)
    {
        if (!_webViewReady) return;
        _ = webViewOutput.CoreWebView2.ExecuteScriptAsync($"removeThinking({JsString(id)})");
    }

    /// <summary>
    /// Returns the markdown that should be rendered in the WebView2 tab for a
    /// given block. For assistant markdown two transformations are layered:
    /// <list type="number">
    ///   <item><see cref="ImageReferenceTransformer"/> appends inline
    ///         <c>![alt](data:...)</c> thumbnails beneath each image-file
    ///         reference. The original path text is preserved.</item>
    ///   <item><see cref="FilePathLinkTransformer"/> rewrites every
    ///         resolvable file-path reference (including image paths)
    ///         as a markdown link of the form
    ///         <c>[path](kp-path:encoded)</c> so the WebView2 click handler
    ///         can shell-execute it (left-click) or reveal it in Explorer
    ///         (right-click). For images the link wraps the path text and
    ///         the thumbnail still appears beneath it.</item>
    /// </list>
    /// Other block kinds and the Raw transcript are unaffected, so the Raw
    /// tab always shows exactly what Copilot emitted.
    /// </summary>
    private string TransformBlockContentForRender(OutputBlock block, string? content)
    {
        if (content == null) return "";
        if (!block.IsMarkdown || block.Kind != BlockKind.Assistant) return content;
        var workspace = _copilot.WorkingDirectory;
        var withThumbs = ImageReferenceTransformer.Apply(content, workspace);
        return FilePathLinkTransformer.Apply(withThumbs, workspace);
    }

    /// <summary>Wraps a string as a JS string literal, escaping special characters.</summary>
    /// <remarks>
    /// Line endings are normalized to LF (\n) and stray C0 control characters
    /// (other than \t and \n) are dropped. Otherwise the WebView's pre-wrap
    /// rendering shows lone CR / control bytes as tofu boxes.
    /// </remarks>
    private static string JsString(string? value)
    {
        if (value == null) return "\"\"";
        var sanitized = SanitizeForJs(value);
        var escaped = sanitized
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t")
            .Replace("<", "\\x3c")
            .Replace(">", "\\x3e");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Normalizes CRLF/CR to LF and strips C0 control characters (except \t, \n)
    /// so they don't render as tofu boxes in the WebView's pre-wrap blocks.
    /// </summary>
    private static string SanitizeForJs(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\r')
            {
                // Collapse CRLF and bare CR to a single LF.
                sb.Append('\n');
                if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                continue;
            }
            if (c == '\n' || c == '\t')
            {
                sb.Append(c);
                continue;
            }
            // VT (0x0B, RichTextBox Shift+Enter soft break), FF (0x0C),
            // NEL (0x85), Unicode line/paragraph separators -> LF.
            if (c == '\v' || c == '\f' || c == '\u0085' || c == '\u2028' || c == '\u2029')
            {
                sb.Append('\n');
                continue;
            }
            // Drop other C0 control chars (0x00-0x1F) and DEL (0x7F).
            if (c < 0x20 || c == 0x7F) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Escapes text for safe insertion into HTML within JS calls.</summary>
    private static string EscapeForJs(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    // ── Raw output (RichTextBox) ─────────────────────────────────────────────

    private void AppendOutput(string text, Color color)
    {
        AppendRaw(text, color);

        // Mirror to the Rendered (WebView) tab so both tabs show the same content.
        MirrorMetaToWebView(text, color);
    }

    /// <summary>
    /// Appends to the Raw RichTextBox only -- does NOT mirror to the Rendered
    /// (WebView2) tab.  Used from <see cref="AppendMessage"/>, where the
    /// Rendered tab is already populated by <see cref="AppendRenderedMessage"/>
    /// with a properly structured (Markdown / tool / sub-agent) block.
    /// Mirroring here would duplicate the assistant response as a plain-text
    /// Status block immediately under its Markdown rendering.
    /// </summary>
    private void AppendRaw(string text, Color color)
    {
        // Suppress redraws for the entire append + scroll sequence.
        // ScrollToCaret must happen while WM_SETREDRAW is still false so the scroll
        // position is already at the bottom when painting resumes.  If ScrollToCaret
        // were called after Invalidate() the control would queue a WM_PAINT at the old
        // scroll position, then scroll (BitBlt), then paint the new bottom strip --
        // producing the "top-half jitter" seen during streaming.
        SendMessage(richTextBoxOutput.Handle, WM_SETREDRAW, false, 0);
        try
        {
            AppendColoredText(richTextBoxOutput, text, color);
            richTextBoxOutput.ScrollToCaret();
        }
        finally
        {
            SendMessage(richTextBoxOutput.Handle, WM_SETREDRAW, true, 0);
            richTextBoxOutput.Invalidate();
        }
    }

    /// <summary>
    /// Appends plain-text output (setup banners, status lines, /help text, etc.) to
    /// the Rendered tab as a rolling Status or Error block. Consecutive AppendOutput
    /// calls of the same color coalesce into one block; the block is closed whenever
    /// a structured WebView block is appended via <see cref="WebViewAppendBlock"/> or
    /// the output is cleared via <see cref="WebViewClearAll"/>.
    /// </summary>
    private void MirrorMetaToWebView(string text, Color color)
    {
        if (!_webViewReady) return;
        if (string.IsNullOrEmpty(text)) return;

        BlockKind kind = (color == AppTheme.ColorError) ? BlockKind.Error : BlockKind.Status;

        if (_currentMetaBlock == null
            || _currentMetaBlock.Kind != kind
            || _currentMetaColor != color)
        {
            _currentMetaBlock = new OutputBlock(kind);
            _currentMetaColor = color;
            _currentMetaBlock.Content = text;
            _outputBlocks.Add(_currentMetaBlock);
            WebViewAppendBlockInternal(_currentMetaBlock);
        }
        else
        {
            _currentMetaBlock.Content += text;
            WebViewUpdateBlock(_currentMetaBlock);
        }
    }

    private static void AppendColoredText(RichTextBox box, string text, Color color)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color;
        box.AppendText(text);
        // TextBoxBase.AppendText restores the selection to the pre-append position
        // (old TextLength), leaving the caret at the START of the appended text rather
        // than the end.  Explicitly move to the true end so ScrollToCaret does not
        // jump back on the next delta.
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = box.ForeColor;
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    private void UpdateWorkingState()
    {
        bool working = _pendingCount > 0;
        // Send requires both an active CLI connection AND an open workspace.
        // The CLI can be connected without a workspace (e.g. after browsing
        // Past Sessions which calls EnsureStartedAsync) -- sending in that
        // state would create a session whose ID falls back to the literal
        // "GoPilot" prefix and whose workspaceFolder is permanently blank.
        buttonSend.Enabled = _copilot.IsConnected
            && !string.IsNullOrEmpty(_copilot.WorkingDirectory);
        buttonStop.Enabled = working;

        string status;
        if (working)
        {
            var kb = _totalBytesReceived > 0 ? $" · {_totalBytesReceived / 1024:F1} KiB" : "";
            status = $"Working…{kb}";
        }
        else
        {
            status = _copilot.IsConnected ? "Connected" : "Not connected";
        }
        toolStripStatusLabelConnection.Text = status;
        UpdateAgentStatus();
    }

    private void UpdateAgentStatus()
    {
        string msg;
        if (_pendingCount == 0)
        {
            msg = "Ready for next command";
        }
        else if (_activeSubAgents.Count == 0)
        {
            msg = "Working…";
        }
        else
        {
            var names = _activeSubAgents.Values.ToList();
            string agentPart = names.Count switch
            {
                1 => $"Waiting for {names[0]}",
                2 => $"Agents: {names[0]} · {names[1]}",
                3 => $"Agents: {names[0]} · {names[1]} · {names[2]}",
                _ => $"{names.Count} agents active",
            };
            string donePart = _completedAgentCount > 0
                ? $" ({_completedAgentCount} done)"
                : "";
            msg = agentPart + donePart;
        }
        toolStripStatusLabelAgentStatus.Text = msg;
    }

    // Used by long-running operations (e.g. session refresh handoff) which need
    // to block the UI exclusively
    private void SetSendingState(bool isSending)
    {
        buttonSend.Enabled = !isSending
            && _copilot.IsConnected
            && !string.IsNullOrEmpty(_copilot.WorkingDirectory);
        buttonStop.Enabled = isSending || _pendingCount > 0;
        toolStripStatusLabelConnection.Text = isSending ? "Working…" :
            (_copilot.IsConnected ? "Connected" : "Not connected");
    }

    private void UpdateConnectionStatus(string status)
    {
        if (status == "Connected")
        {
            _reconnecting = false;
            UpdateWorkingState();
            _ = ShowCliVersionAsync();
        }
        else
        {
            if (status == "Reconnecting...")
            {
                // Session was lost mid-conversation; clear pending state so the UI
                // doesn't stay stuck on "Working…" while the reconnect happens.
                if (_pendingCount > 0 || _mainSessionId != null)
                    AppendOutput("\r\n⚠️ Session lost — reconnecting…\r\n\r\n", AppTheme.ColorError);

                _reconnecting = true;
                ResetSessionTrackingState();
                _mainSessionId = null;
            }
            else if (status == "Not connected" && _reconnecting)
            {
                // Automatic reconnect attempt failed.
                AppendOutput("\r\n❌ Session lost and reconnect failed. Open a folder to reconnect.\r\n\r\n",
                    AppTheme.ColorError);
                _reconnecting = false;
                ResetSessionTrackingState();
                _mainSessionId = null;
            }

            toolStripStatusLabelConnection.Text = status;
            buttonSend.Enabled = false;
            buttonStop.Enabled = false;
        }
    }

    // ── Folder trust ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="folderPath"/> is already recorded as a
    /// trusted location in the CLI's permissions-config.json.  If not, prompts
    /// the user; on approval the path is written to that shared file so both
    /// GoPilot and the Copilot CLI recognise it as trusted going forward.
    /// Returns false (without connecting) if the user declines.
    /// </summary>
    private static bool EnsureFolderTrusted(string folderPath)
    {
        if (CopilotPermissionsConfig.IsTrusted(folderPath)) return true;

        var result = MessageBox.Show(
            $"Do you want to allow Copilot to access this folder?\r\n\r\n" +
            $"{folderPath}\r\n\r\n" +
            "Copilot can read files, write files, and run shell commands here.",
            "Trust this folder?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes) return false;

        CopilotPermissionsConfig.Trust(folderPath);
        return true;
    }

    // ── Permission / input dialogs ────────────────────────────────────────────

    private void Copilot_PermissionRequested(object? sender, PermissionEventArgs args)
    {
        // Called on SDK background thread; Invoke marshals to UI thread.
        if (!IsHandleCreated) { args.Decision.TrySetResult(false); return; }
        Invoke(() =>
        {
            SoundService.PlayDialog();
            ShowFloatingDialog(new PermissionDialog(args));
        });
    }

    private void Copilot_UserInputRequested(object? sender, UserInputEventArgs args)
    {
        if (!IsHandleCreated) { args.Answer.TrySetResult(""); return; }
        Invoke(() =>
        {
            SoundService.PlayDialog();
            ShowFloatingDialog(new UserInputDialog(args));
        });
    }

    /// <summary>
    /// Shows a Copilot-driven question/permission dialog modelessly so the user
    /// can still scroll and interact with the output panel while answering.
    /// The dialog floats above the main window (TopMost + Owner) and disposes
    /// itself on close. Result is communicated back to the SDK through the
    /// TaskCompletionSource on its event-args, not via DialogResult.
    /// </summary>
    private void ShowFloatingDialog(Form dialog)
    {
        dialog.Owner = this;
        dialog.TopMost = true;
        dialog.ShowInTaskbar = false;
        dialog.FormClosed += (_, _) => dialog.Dispose();
        dialog.Show(this);
        dialog.Activate();
    }

    // ── Update checking ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks NuGet for a newer GitHub.Copilot.SDK release and notifies the user
    /// if one is found. Runs asynchronously at startup without blocking the UI.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        var current = UpdateChecker.GetCurrentSdkVersion();
        string? latest;

        try
        {
            latest = await UpdateChecker.GetLatestSdkVersionAsync();
        }
        catch
        {
            latest = null;
        }

        if (latest == null)
        {
            AppendOutput($"[SDK v{current} — update check unavailable]\r\n", AppTheme.ColorMeta);
            return;
        }

        if (UpdateChecker.IsNewer(current, latest))
        {
            AppendOutput(
                $"[Update available: SDK v{current} -> v{latest}]\r\n",
                AppTheme.ColorTool);
        }
        else
        {
            AppendOutput($"[SDK v{current} — up to date]\r\n", AppTheme.ColorMeta);
        }
    }

    /// <summary>Displays the running Copilot CLI version in the output panel after a connection.</summary>
    private async Task ShowCliVersionAsync()
    {
        var version = await _copilot.GetVersionAsync();
        if (string.IsNullOrEmpty(version)) return;

        var source = _copilot.IsCliFromPath ? "system" : "bundled";

        if (_cliUpdateChecked || !_copilot.IsCliFromPath)
        {
            AppendOutput($"[Copilot CLI v{version} ({source})]\r\n", AppTheme.ColorMeta);
            if (!_copilot.IsCliFromPath)
                AppendOutput(
                    "[To use your system CLI instead, install it via: winget install GitHub.CopilotCLI — then restart GoPilot]\r\n",
                    AppTheme.ColorMeta);
            return;
        }

        _cliUpdateChecked = true;

        var latest = await UpdateChecker.GetLatestCliVersionAsync();

        if (latest == null)
        {
            AppendOutput($"[Copilot CLI v{version} ({source}) — update check unavailable]\r\n", AppTheme.ColorMeta);
            return;
        }

        if (UpdateChecker.IsNewer(version, latest))
        {
            AppendOutput(
                $"[Copilot CLI update available: v{version} -> v{latest} ({source}) — open a Copilot terminal and run /update]\r\n",
                AppTheme.ColorTool);
        }
        else
        {
            AppendOutput($"[Copilot CLI v{version} ({source}) — up to date]\r\n", AppTheme.ColorMeta);
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _subAgentWatchdog.Stop();
        _subAgentWatchdog.Dispose();
        _ = _copilot.DisposeAsync().AsTask();
    }

    private void webViewOutput_Click(object sender, EventArgs e)
    {

    }
}
