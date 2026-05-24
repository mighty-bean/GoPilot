using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoPilot;

public enum MessageKind
{
    AssistantDelta,
    AssistantFinal,
    Reasoning,
    ToolStart,
    ToolComplete,
    ToolProgress,
    SubAgentStart,
    SubAgentComplete,
    SubAgentFailed,
    SkillInvoked,
    CustomAgentsUpdated,
    BytesUpdate,
    Error,
    Status,
}

public sealed class SessionEventArgs : EventArgs
{
    public string SessionId { get; init; } = "";
    public bool IsSubAgent { get; init; }
}

public sealed class SessionMessageEventArgs : EventArgs
{
    public string  SessionId          { get; init; } = "";
    public string  Content            { get; init; } = "";
    public MessageKind Kind           { get; init; }
    public string? ToolCallId         { get; init; }
    public string? ParentToolCallId   { get; init; }
    // ToolStart extras
    public string? ToolArgSummary     { get; init; }
    // ToolComplete extras
    public bool    ToolSuccess        { get; init; }
    public string? ToolResultSummary  { get; init; }
    // SubAgent extras
    public string? SubAgentDisplayName { get; init; }
    public string? SubAgentDescription { get; init; }
    // SubAgent completion stats
    public string? SubAgentModel       { get; init; }
    public double? SubAgentTotalCalls  { get; init; }
    public double? SubAgentTotalTokens { get; init; }
    public double? SubAgentDurationMs  { get; init; }
    // BytesUpdate
    public double  TotalBytes          { get; init; }
}

public sealed class PermissionEventArgs : EventArgs
{
    public string OperationKind { get; init; } = "";
    public string? ToolName { get; init; }
    public string? FileName { get; init; }
    public string? CommandText { get; init; }
    /// <summary>Set by the UI before resolving Decision to true, to approve all
    /// future requests of the same OperationKind for the rest of the session.</summary>
    public bool ApproveSimilar { get; set; }
    public TaskCompletionSource<bool> Decision { get; } = new();
}

public sealed class UserInputEventArgs : EventArgs
{
    public string Question { get; init; } = "";
    public IReadOnlyList<string>? Choices { get; init; }
    public bool AllowFreeform { get; init; } = true;
    public TaskCompletionSource<string> Answer { get; } = new();
}

public sealed class ContextUsageEventArgs : EventArgs
{
    public string SessionId       { get; init; } = "";
    public double InputTokens     { get; init; }
    public double MaxPromptTokens { get; init; }
    public double Percent => MaxPromptTokens > 0 ? (InputTokens / MaxPromptTokens) * 100.0 : 0;
}

public sealed class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    private CopilotSession? _mainSession;
    private bool _isConnected;
    private IDisposable? _lifecycleSubscription;
    private IDisposable? _lifecycleDeletedSubscription;
    private readonly Dictionary<string, CopilotSession> _sessions = new();
    private readonly Dictionary<string, string> _pendingToolNames = new();
    private readonly Dictionary<string, string> _toolCallToName = new();
    // Kinds approved for the rest of this session via "Approve Similar"
    private readonly HashSet<string> _approvedKinds = new();
    // Latest SDK-registered slash commands from CommandsChangedEvent (main session only)
    private CommandsChangedCommand[] _cachedSdkCommands = [];
    // Status messages produced before the session ID is known; emitted once the session is created.
    private readonly Queue<string> _pendingStatusMessages = new();

    private CancellationTokenSource? _keepAliveCts;
    private string? _cliPath;
    private const int KeepAliveIntervalSeconds = 30;

    public event EventHandler<SessionEventArgs>? SessionCreated;
    public event EventHandler<SessionMessageEventArgs>? MessageReceived;
    public event EventHandler<PermissionEventArgs>? PermissionRequested;
    public event EventHandler<UserInputEventArgs>? UserInputRequested;
    public event EventHandler<string>? ConnectionStateChanged;
    public event EventHandler<string>? SessionIdleForSession;
    /// <summary>
    /// Fires when a non-main, non-internal session is destroyed by the CLI.
    /// The UI uses this as a reliable "sub-agent session truly ended" signal so it
    /// can recover from missing or out-of-order subagent.completed/failed events.
    /// </summary>
    public event EventHandler<string>? SubAgentSessionEnded;
    public event EventHandler<ContextUsageEventArgs>? ContextUsageChanged;

    // Most recent input-token reading from the main session (size of the prompt
    // window the model just consumed) and the active model's prompt-token ceiling.
    private double _currentInputTokens     = 0;
    private double _currentMaxPromptTokens = 0;
    // Cache of per-model prompt-token limits, populated by ListModelsAsync.
    private readonly Dictionary<string, double> _modelPromptLimits =
        new(StringComparer.OrdinalIgnoreCase);
    // Cache of per-model supported reasoning-effort levels (only populated for
    // models where capabilities.supports.reasoningEffort is true).
    private readonly Dictionary<string, IReadOnlyList<string>> _modelSupportedEfforts =
        new(StringComparer.OrdinalIgnoreCase);
    // Cache of per-model SDK-reported default reasoning-effort level. GoPilot
    // does NOT use this for selection (per-spec we always default to the
    // highest available); kept for diagnostic / future-feature use only.
    private readonly Dictionary<string, string> _modelDefaultEffort =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _modelLimitsLogged = false;

    public double CurrentInputTokens     => _currentInputTokens;
    public double CurrentMaxPromptTokens => _currentMaxPromptTokens;

    public string ActiveModel { get; set; } = "claude-sonnet-4.6";
    public string ActiveMode  { get; set; } = "Standard";

    /// <summary>
    /// Reasoning-effort level applied to the active model when the model
    /// supports it (one of "low", "medium", "high", "xhigh"). Null when the
    /// active model does not advertise reasoning-effort support.
    /// </summary>
    public string? ActiveReasoningEffort { get; set; }
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Per-workspace data root under %LOCALAPPDATA%\GoPilot\workspaces\&lt;key&gt;\
    /// where key = sanitized leaf folder name + truncated SHA-256 of the
    /// normalized full path. Holds scratch\, webview2\, and dreams\ subfolders.
    /// </summary>
    public string? WorkspaceDataPath => WorkingDirectory == null ? null
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GoPilot",
            "workspaces",
            ComputeWorkspaceKey(WorkingDirectory));

    /// <summary>
    /// Scratch directory advertised to the model in the system message.
    /// All Copilot-authored temp files (SQLite, logs, intermediates) go here.
    /// </summary>
    public string? ScratchpadPath => WorkspaceDataPath == null ? null
        : Path.Combine(WorkspaceDataPath, "scratch");

    private static string ComputeWorkspaceKey(string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var leaf = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(leaf))
        {
            // Drive root (e.g. "D:\") has no file-name component
            leaf = normalized.Replace(":", "").Replace("\\", "").Replace("/", "");
            if (string.IsNullOrEmpty(leaf)) leaf = "root";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safeLeaf = new string(leaf.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        var bytes = Encoding.UTF8.GetBytes(normalized.ToLowerInvariant());
        var hash  = SHA256.HashData(bytes);
        var hex   = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();

        return $"{safeLeaf}-{hex}";
    }
    /// <summary>
    /// Ordered list of Skill Tree folders contributing skills/, agents/, and
    /// gopilot-instructions.md tiers (set from gopilot.ini).
    /// </summary>
    public IList<string> SkillTreeFolders { get; set; } = new List<string>();
    public bool AutoApprove { get; set; } = false;
    public bool FleetMode   { get; set; } = false;
    /// <summary>
    /// When true, every new session's system message includes a directive
    /// instructing the model to respond in token-minimal "caveman speak".
    /// Mirrored from GoPilot's Session menu and persisted in gopilot.ini.
    /// </summary>
    public bool CavemanMode { get; set; } = false;
    public bool IsConnected => _client != null && _isConnected;

    // ── Reference cache (populated by LoadTier* during session creation) ────
    private List<AgentInfo>  _cachedAgents  = new();
    private List<SkillInfo>  _cachedSkills  = new();
    private List<PromptInfo> _cachedPrompts = new();
    /// <summary>Agents discovered across all tier folders, sorted by name.</summary>
    public IReadOnlyList<AgentInfo>  CachedAgents  => _cachedAgents;
    /// <summary>Skills discovered across all tier folders, sorted by name.</summary>
    public IReadOnlyList<SkillInfo>  CachedSkills  => _cachedSkills;
    /// <summary>Prompts (prompts/*.md) discovered across all tier folders, sorted by name.</summary>
    public IReadOnlyList<PromptInfo> CachedPrompts => _cachedPrompts;

    /// <summary>
    /// Re-scans tier folders and rebuilds the agent/skill/prompt caches without
    /// touching any active session. Use when the Skill Tree changes and the
    /// UI wants fresh lists before the next session is created.
    /// </summary>
    public void RebuildReferenceCache()
    {
        _ = LoadTierAgents();
        _ = LoadTierSkillDirectories();
        LoadTierPrompts();
    }

    public async Task<string> GetVersionAsync()
    {
        if (_client == null) return "";
        try
        {
            // The version reported by GetStatusAsync() is the internal server/protocol
            // version (e.g. 1.0.16), which is on a different scale to the release
            // version surfaced by "copilot --version" (e.g. 1.0.49) and the GitHub
            // Releases API.  When we have the system CLI path, run the binary directly
            // to get the user-facing release version so that the update check compares
            // like with like.
            if (_cliPath != null)
            {
                var binaryVersion = await GetCliBinaryVersionAsync(_cliPath);
                if (!string.IsNullOrEmpty(binaryVersion))
                    return binaryVersion;
            }

            var status = await _client.GetStatusAsync();
            return status.Version ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Runs <c>{path} --version</c> and parses the version number from the output.
    /// Expected output: "GitHub Copilot CLI 1.0.49." (trailing period is stripped).
    /// Returns null on any failure.
    /// </summary>
    private static async Task<string?> GetCliBinaryVersionAsync(string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var psi = new System.Diagnostics.ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            // "GitHub Copilot CLI 1.0.49.\nRun 'copilot update'..."
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd('.');
                // Find the last whitespace-delimited token that looks like a version
                var parts = trimmed.Split(' ');
                var last = parts[^1];
                if (last.Length > 0 && char.IsDigit(last[0]))
                    return last;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the client if needed and returns the available model IDs from the SDK.
    /// Returns an empty list on any error (e.g., not authenticated); fires
    /// <see cref="ConnectionStateChanged"/> with the error message so the UI can surface it.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync()
    {
        try
        {
            await EnsureStartedAsync();
            var models = await _client!.ListModelsAsync();

            // Capture per-model prompt-token ceiling for the prompt-window meter.
            foreach (var m in models)
            {
                if (string.IsNullOrEmpty(m.Id)) continue;
                var limits = m.Capabilities.Limits;
                var max = limits.MaxPromptTokens
                       ?? (limits.MaxContextWindowTokens > 0 ? limits.MaxContextWindowTokens : 0);
                if (max > 0) _modelPromptLimits[m.Id] = max;

                // Capture reasoning-effort capability so the UI can show an
                // Effort dropdown only for models that support it.
                if (m.Capabilities.Supports?.ReasoningEffort == true
                    && m.SupportedReasoningEfforts is { Count: > 0 } efforts)
                {
                    _modelSupportedEfforts[m.Id] = efforts.ToList();
                }
                if (!string.IsNullOrEmpty(m.DefaultReasoningEffort))
                    _modelDefaultEffort[m.Id] = m.DefaultReasoningEffort;
            }

            // Reflect the current model's limit immediately so the meter has a
            // denominator before the first turn arrives.
            _currentMaxPromptTokens = LookupMaxPromptTokens(ActiveModel);

            if (!_modelLimitsLogged && _modelPromptLimits.Count > 0)
            {
                _modelLimitsLogged = true;
                var summary = string.Join(", ", _modelPromptLimits
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}={FormatTokensShort(kvp.Value)}"));
                EmitStatus($"Model prompt limits loaded: {summary}");
            }

            return models
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
        }
        catch (Exception ex)
        {
            ConnectionStateChanged?.Invoke(this, $"Model list unavailable: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Returns the prompt-token ceiling for <paramref name="modelId"/>.
    /// Only trusts SDK-reported limits gathered from ListModelsAsync.
    /// If the SDK has not reported limits yet, returns 0 so the UI shows an unknown denominator
    /// rather than guessing (different accounts/models can have different caps).
    /// </summary>
    private double LookupMaxPromptTokens(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return 0;
        if (_modelPromptLimits.TryGetValue(modelId, out var v) && v > 0) return v;
        return 0;
    }

    /// <summary>
    /// Returns the SDK-reported list of supported reasoning-effort levels for
    /// <paramref name="modelId"/>, or an empty list if the model does not
    /// support reasoning effort (or the SDK has not yet reported its
    /// capabilities). Order matches what the SDK provided.
    /// </summary>
    public IReadOnlyList<string> GetSupportedReasoningEfforts(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return Array.Empty<string>();
        return _modelSupportedEfforts.TryGetValue(modelId, out var efforts)
            ? efforts
            : Array.Empty<string>();
    }

    /// <summary>
    /// Returns the SDK-reported default reasoning-effort level for
    /// <paramref name="modelId"/>, or null if the model does not support
    /// reasoning effort (or the SDK has not yet reported its capabilities).
    /// GoPilot does not use this for selection (UI always picks the highest
    /// available level); kept for diagnostics and future configurability.
    /// </summary>
    public string? GetDefaultReasoningEffort(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        return _modelDefaultEffort.TryGetValue(modelId, out var v) ? v : null;
    }

    /// <summary>
    /// Returns the highest available reasoning-effort level for
    /// <paramref name="modelId"/>, ranked xhigh > high > medium > low. Falls
    /// back to whichever SDK-reported value sorts last alphabetically when an
    /// unrecognised level is encountered. Null when the model does not support
    /// reasoning effort.
    /// </summary>
    public string? GetHighestReasoningEffort(string? modelId)
    {
        var efforts = GetSupportedReasoningEfforts(modelId);
        if (efforts.Count == 0) return null;
        return efforts
            .OrderByDescending(RankReasoningEffort)
            .ThenByDescending(e => e, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>
    /// Numeric rank for the well-known reasoning-effort tiers so the highest
    /// tier wins regardless of SDK list order. Unknown tiers rank below the
    /// well-known set so future SDK additions surface (sorted alphabetically)
    /// without crowding out the documented winner.
    /// </summary>
    internal static int RankReasoningEffort(string effort) => effort?.ToLowerInvariant() switch
    {
        "xhigh"  => 4,
        "high"   => 3,
        "medium" => 2,
        "low"    => 1,
        _        => 0,
    };

    // True when an AssistantUsageEvent represents a top-level user-driven
    // request, as opposed to a sub-agent or sampling call whose tokens should
    // not be charged to the visible context-window meter.  The SDK schema says
    // Initiator is absent for user-initiated calls, but some models emit the
    // literal string "user", so we accept both forms.  Known non-user values
    // ("sub-agent", "mcp-sampling", and any future additions) are rejected.
    private static bool IsUserInitiatedUsage(string? initiator)
    {
        if (string.IsNullOrEmpty(initiator)) return true;
        return string.Equals(initiator, "user", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the slash commands currently available in the active session.
    /// Merges SDK-registered commands (from CommandsChangedEvent) with user-invocable
    /// skills obtained via an on-demand skills.list RPC call.  Returns an empty list
    /// when no session exists.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string? Description)>> GetCommandListAsync()
    {
        if (_mainSession == null) return [];

        var results = new List<(string Name, string? Description)>();

        // SDK-registered commands (kept current via CommandsChangedEvent)
        foreach (var cmd in _cachedSdkCommands)
            results.Add((cmd.Name, cmd.Description));

        // User-invocable skills are also slash-commandable — fetch on demand
        try
        {
#pragma warning disable GHCP001
            var skillsResult = await _mainSession.Rpc.Skills.ListAsync();
#pragma warning restore GHCP001
            foreach (var skill in skillsResult.Skills)
            {
                if (!skill.UserInvocable || !skill.Enabled) continue;
                if (!results.Exists(r => string.Equals(r.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
                    results.Add((skill.Name, string.IsNullOrWhiteSpace(skill.Description) ? null : skill.Description));
            }
        }
        catch { /* best-effort; SDK commands already captured above */ }

        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return results;
    }

    public async Task<string> SendAndCaptureResponseAsync(string prompt, TimeSpan? timeout = null)
    {
        await EnsureStartedAsync();
        if (_mainSession == null)
            await CreateMainSessionAsync();

        var sb = new System.Text.StringBuilder();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<SessionMessageEventArgs>? msgHandler  = null;
        EventHandler<string>?                  idleHandler = null;

        msgHandler = (_, args) =>
        {
            if (args.SessionId != _mainSession?.SessionId) return;
            if (args.Kind is MessageKind.AssistantDelta or MessageKind.AssistantFinal)
                sb.Append(args.Content);
        };

        idleHandler = (_, sessionId) =>
        {
            if (sessionId != _mainSession?.SessionId) return;
            MessageReceived         -= msgHandler;
            SessionIdleForSession   -= idleHandler;
            tcs.TrySetResult(sb.ToString());
        };

        MessageReceived       += msgHandler;
        SessionIdleForSession += idleHandler;

        try
        {
            await _mainSession!.SendAsync(new MessageOptions { Prompt = prompt });

            var delay = timeout ?? TimeSpan.FromMinutes(5);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(delay));
            if (winner != tcs.Task)
            {
                MessageReceived       -= msgHandler;
                SessionIdleForSession -= idleHandler;
                throw new TimeoutException("Copilot did not respond within the timeout period.");
            }
            return await tcs.Task;
        }
        catch
        {
            MessageReceived       -= msgHandler;
            SessionIdleForSession -= idleHandler;
            throw;
        }
    }

    public async Task UpdateModelAsync(string model, string? reasoningEffort = null)
    {
        ActiveModel = model;
        ActiveReasoningEffort = NormalizeReasoningEffortFor(model, reasoningEffort);
        _currentMaxPromptTokens = LookupMaxPromptTokens(model);

        // Immediately refresh the UI meter against the new denominator.
        ContextUsageChanged?.Invoke(this, new ContextUsageEventArgs
        {
            SessionId       = _mainSession?.SessionId ?? "",
            InputTokens     = _currentInputTokens,
            MaxPromptTokens = _currentMaxPromptTokens,
        });

        var effortSuffix = string.IsNullOrEmpty(ActiveReasoningEffort)
            ? ""
            : $" (effort: {ActiveReasoningEffort})";
        EmitStatus(_currentMaxPromptTokens > 0
            ? $"Model set to {model}{effortSuffix}. Prompt window: {FormatTokensShort(_currentMaxPromptTokens)} tokens."
            : $"Model set to {model}{effortSuffix}. Prompt window limit not yet known.");

        if (_mainSession != null)
        {
            try
            {
                if (string.IsNullOrEmpty(ActiveReasoningEffort))
                    await _mainSession.SetModelAsync(model);
                else
                    await _mainSession.SetModelAsync(model, ActiveReasoningEffort);
            }
            catch { /* best-effort; new model applied on next session if this fails */ }
        }
    }

    /// <summary>
    /// Updates the reasoning-effort level on the active session in place via
    /// the SDK's runtime model-switch API, without recreating the session.
    /// No-op when the active model does not support reasoning effort or when
    /// the requested level matches what is already active.
    /// </summary>
    public async Task UpdateReasoningEffortAsync(string? reasoningEffort)
    {
        var normalized = NormalizeReasoningEffortFor(ActiveModel, reasoningEffort);
        if (string.Equals(normalized, ActiveReasoningEffort, StringComparison.OrdinalIgnoreCase))
            return;

        ActiveReasoningEffort = normalized;
        EmitStatus(string.IsNullOrEmpty(normalized)
            ? $"Reasoning effort cleared (model {ActiveModel} does not support it)."
            : $"Reasoning effort set to {normalized}.");

        if (_mainSession != null && !string.IsNullOrEmpty(normalized))
        {
            try { await _mainSession.SetModelAsync(ActiveModel, normalized); }
            catch { /* best-effort; takes effect on next session if this fails */ }
        }
    }

    /// <summary>
    /// Validates a requested reasoning-effort level against the model's SDK-
    /// reported supported set. Returns null when the model does not support
    /// reasoning effort, or when the requested value is not in the supported
    /// set (callers should re-pick a supported value, typically the highest).
    /// </summary>
    private string? NormalizeReasoningEffortFor(string? modelId, string? requested)
    {
        var supported = GetSupportedReasoningEfforts(modelId);
        if (supported.Count == 0) return null;
        if (string.IsNullOrEmpty(requested)) return null;
        return supported.FirstOrDefault(e =>
            string.Equals(e, requested, StringComparison.OrdinalIgnoreCase));
    }

    public async Task EnsureStartedAsync()
    {
        if (_client != null) return;

        if (ScratchpadPath != null)
            Directory.CreateDirectory(ScratchpadPath);

        await ConnectAsync();
        StartKeepAlive();
    }

    /// <summary>
    /// True when the CLI binary was resolved from PATH; false when the SDK is using its bundled copy.
    /// Set on each call to <see cref="ConnectAsync"/>.
    /// </summary>
    public bool IsCliFromPath { get; private set; }

    private async Task ConnectAsync()
    {
        ConnectionStateChanged?.Invoke(this, "Connecting...");

        var cliPath = ResolveCliFromPath();

        // Guard against an outdated system CLI that would be protocol-incompatible with
        // this SDK build. The bundled CLI was packaged with the SDK and is guaranteed to
        // support all required protocol features.  The system CLI's *file* version (not its
        // self-reported --version string, which reflects the package release version) is
        // compared: on this machine the WinGet-installed binary may report "1.0.49" via
        // --version but carry file version 1.0.16, which uses the old v1 permission protocol
        // and produces "Unhandled permission result kind: [object Object]" errors at runtime.
        if (cliPath != null)
        {
            var bundledVer = GetBundledCliFileVersion();
            var systemVer  = GetExeFileVersion(cliPath);
            if (bundledVer != null && systemVer != null && systemVer < bundledVer)
            {
                EmitStatus(
                    $"[GoPilot] System CLI (file version {systemVer.Major}.{systemVer.Minor}.{systemVer.Build}) " +
                    $"is older than the bundled CLI ({bundledVer.Major}.{bundledVer.Minor}.{bundledVer.Build}). " +
                    $"Using the bundled CLI to ensure SDK protocol compatibility. " +
                    $"Run 'copilot update' in a terminal to upgrade your system CLI.");
                cliPath = null;
            }
        }

        IsCliFromPath = cliPath != null;
        _cliPath = cliPath;

        _client = new CopilotClient(new CopilotClientOptions
        {
            Connection = cliPath != null
                ? RuntimeConnection.ForStdio(cliPath, new List<string>())
                : null,
            WorkingDirectory = WorkingDirectory,
            Environment = BuildCliEnvironment(),
        });

        _lifecycleSubscription = _client.OnLifecycle<SessionCreatedEvent>(evt =>
        {
            if (evt.SessionId != _mainSession?.SessionId)
            {
                SessionCreated?.Invoke(this, new SessionEventArgs
                {
                    SessionId = evt.SessionId,
                    IsSubAgent = true,
                });
            }
        });

        _lifecycleDeletedSubscription = _client.OnLifecycle<SessionDeletedEvent>(evt =>
        {
            // The main session being deleted is handled elsewhere (reconnect/reset paths).
            if (evt.SessionId == _mainSession?.SessionId) return;

            SubAgentSessionEnded?.Invoke(this, evt.SessionId);
        });

        await _client.StartAsync();
        _isConnected = true;
        ConnectionStateChanged?.Invoke(this, "Connected");
    }

    private void StartKeepAlive()
    {
        if (_keepAliveCts != null) return;
        _keepAliveCts = new CancellationTokenSource();
        _ = RunKeepAliveAsync(_keepAliveCts.Token);
    }

    private void StopKeepAlive()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
    }

    private async Task RunKeepAliveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(KeepAliveIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_client == null) continue;
                try
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await _client.PingAsync(cancellationToken: pingCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    await TryReconnectAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        ConnectionStateChanged?.Invoke(this, "Reconnecting...");

        _lifecycleSubscription?.Dispose();
        _lifecycleSubscription = null;
        _lifecycleDeletedSubscription?.Dispose();
        _lifecycleDeletedSubscription = null;

        foreach (var session in _sessions.Values)
            try { await session.DisposeAsync(); } catch { }
        _sessions.Clear();
        _mainSession = null;
        _cachedSdkCommands = [];
        _pendingToolNames.Clear();
        _approvedKinds.Clear();

        if (_client != null)
        {
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
            _isConnected = false;
        }

        if (cancellationToken.IsCancellationRequested) return;

        try
        {
            await ConnectAsync();
            await CreateMainSessionAsync();
        }
        catch (OperationCanceledException) { }
        catch
        {
            ConnectionStateChanged?.Invoke(this, "Not connected");
        }
    }

    public async Task EnsureSessionAsync()
    {
        await EnsureStartedAsync();
        if (_mainSession == null)
            await CreateMainSessionAsync();
    }

    /// <summary>
    /// Generates a session ID from the workspace leaf folder name, date and time.
    /// Format: <c>{LeafFolder}-{MM-dd-yyyy-HHmmss}</c>.
    /// Example: <c>GoPilot-04-24-2026-204941</c>.
    /// </summary>
    public string GenerateSessionId()
    {
        var leaf = !string.IsNullOrEmpty(WorkingDirectory)
            ? new DirectoryInfo(WorkingDirectory).Name
            : "GoPilot";
        return $"{leaf}-{DateTime.Now:MM-dd-yyyy-HHmmss}";
    }

    /// <summary>
    /// Returns the list of persisted session IDs known to the CLI.
    /// Starts the client if needed.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListPersistedSessionsAsync()
    {
        await EnsureStartedAsync();
        try
        {
            var sessions = await _client!.ListSessionsAsync();
            return sessions.Select(s => s.SessionId).ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Permanently deletes a persisted session from the CLI's storage,
    /// then removes the matching <c>~/.copilot/session-state/&lt;id&gt;/</c>
    /// scratch folder if one exists. The CLI and any skills writing into
    /// that folder during the session leave behind plans, checkpoints,
    /// and intermediate logs that the SDK's own delete does not clean
    /// up; without this step the folder becomes orphaned scratch state
    /// that grows without bound.
    ///
    /// Scratch-folder cleanup is best-effort: if the directory is locked
    /// or unreachable the failure is swallowed silently. The SDK delete
    /// has already succeeded by then, so a stale folder is harmless and
    /// retrying on the next attempt is fine.
    /// </summary>
    public async Task DeletePersistedSessionAsync(string sessionId)
    {
        await EnsureStartedAsync();
        await _client!.DeleteSessionAsync(sessionId);
        TryDeleteSessionStateFolder(sessionId);
    }

    private static void TryDeleteSessionStateFolder(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "session-state", sessionId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the SDK delete has already succeeded.
        }
    }

    /// <summary>
    /// Resumes a persisted session as the new main session.
    /// Tears down any existing main session first.
    /// </summary>
    public async Task ResumePersistedSessionAsync(string sessionId)
    {
        await EnsureStartedAsync();

        // Tear down existing main session if any
        if (_mainSession != null)
        {
            await _mainSession.DisposeAsync();
            _sessions.Remove(_mainSession.SessionId);
            _mainSession = null;
        }
        _approvedKinds.Clear();
        _currentInputTokens = 0;

        var agents    = LoadTierAgents();
        var skillDirs = LoadTierSkillDirectories();
        LoadTierPrompts();

        var session = await _client!.ResumeSessionAsync(sessionId, new ResumeSessionConfig
        {
            Model = ActiveModel,
            ReasoningEffort = ActiveReasoningEffort,
            Streaming = true,
            OnPermissionRequest = BuildPermissionHandler(),
            OnUserInputRequest = BuildUserInputHandler(),
            SystemMessage = BuildSystemMessage(),
        });

        _mainSession = session;
        _sessions[session.SessionId] = session;
        session.On<SessionEvent>(evt => HandleSessionEvent(session.SessionId, evt));

        if (AutoApprove || ActiveMode == "Autopilot")
        {
            try
            {
                var r = await session.Rpc.Permissions.SetApproveAllAsync(true);
                if (!r.Success) EmitStatus("[Permission] SetApproveAllAsync returned Success=false.");
            }
            catch (Exception ex) { EmitStatus($"[Permission] SetApproveAllAsync failed: {ex.Message}"); }
        }

        if (FleetMode)
        {
            try
            {
#pragma warning disable GHCP001
                var fleetResult = await session.Rpc.Fleet.StartAsync(
                    prompt: WorkingDirectory != null ? BuildFleetScopeDirective() : null);
#pragma warning restore GHCP001
                if (!fleetResult.Started)
                    throw new InvalidOperationException(
                        "Fleet mode could not be activated.");
            }
            catch
            {
                _sessions.Remove(session.SessionId);
                _mainSession = null;
                try { await session.DisposeAsync(); } catch { }
                throw;
            }
        }

        SessionCreated?.Invoke(this, new SessionEventArgs
        {
            SessionId = session.SessionId,
            IsSubAgent = false,
        });
        EmitPendingStatus(session.SessionId);
    }

    /// <summary>
    /// Retrieves the conversation history from the current main session.
    /// Returns an empty list when no session exists or on error.
    /// </summary>
    public async Task<IReadOnlyList<(string Type, string Content)>> GetSessionMessagesAsync()
    {
        if (_mainSession == null) return [];
        try
        {
            var messages = await _mainSession.GetEventsAsync();
            var result = new List<(string, string)>();
            foreach (var evt in messages)
            {
                switch (evt)
                {
                    case UserMessageEvent user
                        when !string.IsNullOrEmpty(user.Data.Content):
                        result.Add(("user", user.Data.Content));
                        break;
                    case AssistantMessageEvent asst
                        when !string.IsNullOrEmpty(asst.Data.Content):
                        result.Add(("assistant", asst.Data.Content));
                        break;
                }
            }
            return result;
        }
        catch { return []; }
    }

    public string? MainSessionId => _mainSession?.SessionId;

    public async Task SendMessageAsync(string prompt, IReadOnlyList<string> attachmentPaths)
    {
        await EnsureStartedAsync();

        if (_mainSession == null)
            await CreateMainSessionAsync();

        List<UserMessageAttachment>? attachments = null;
        if (attachmentPaths.Count > 0)
        {
            attachments = attachmentPaths
                .Select(BuildAttachment)
                .ToList();
        }

        var options = new MessageOptions { Prompt = prompt, Attachments = attachments };

        try
        {
            await _mainSession!.SendAsync(options);
        }
        catch (IOException ex) when (ex.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
        {
            // The CLI cleaned up the server-side session after an idle period.
            // Recreate the session transparently and retry — the user sees the
            // new session header but their prompt still goes through normally.
            await RecoverExpiredSessionAsync();
            await _mainSession!.SendAsync(options);
        }
    }

    /// <summary>
    /// Builds the SDK attachment for a given path. Images are sent as base64 blob
    /// attachments with their MIME type (required so the model actually sees the
    /// picture); everything else is sent as a path-based file attachment.
    /// </summary>
    private static UserMessageAttachment BuildAttachment(string path)
    {
        var mime = GetImageMimeType(path);
        if (mime != null && File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                return new UserMessageAttachmentBlob
                {
                    Data        = Convert.ToBase64String(bytes),
                    DisplayName = Path.GetFileName(path),
                    MimeType    = mime,
                };
            }
            catch
            {
                // Fall through to a file attachment if we can't read the image.
            }
        }

        return new UserMessageAttachmentFile
        {
            Path        = path,
            DisplayName = Path.GetFileName(path),
        };
    }

    private static string? GetImageMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png"          => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"          => "image/gif",
            ".webp"         => "image/webp",
            ".bmp"          => "image/bmp",
            _               => null,
        };

    /// <summary>
    /// Recovers from a server-side session expiry without tearing down the CLI transport.
    /// Tries to resume the original session first (preserving conversation history); falls
    /// back to creating a fresh session only if the disk data is also gone.
    /// </summary>
    private async Task RecoverExpiredSessionAsync()
    {
        var expiredSessionId = _mainSession?.SessionId;

        if (_mainSession != null)
        {
            var stale = _mainSession;
            _sessions.Remove(stale.SessionId);
            _mainSession = null;
            try { await stale.DisposeAsync(); } catch { }
        }

        // The CLI keeps session data on disk even after unloading it from memory.
        // Resuming with the original ID restores the full conversation history and
        // requires no UI update (session ID is unchanged).
        if (expiredSessionId != null)
        {
            try
            {
                var session = await _client!.ResumeSessionAsync(expiredSessionId, new ResumeSessionConfig
                {
                    Model = ActiveModel,
                    ReasoningEffort = ActiveReasoningEffort,
                    Streaming = true,
                    OnPermissionRequest = BuildPermissionHandler(),
                    OnUserInputRequest = BuildUserInputHandler(),
                    SystemMessage = BuildSystemMessage(),
                });

                _mainSession = session;
                _sessions[session.SessionId] = session;
                session.On<SessionEvent>(evt => HandleSessionEvent(session.SessionId, evt));
                if (AutoApprove || ActiveMode == "Autopilot")
                {
                    try
                    {
                        var r = await session.Rpc.Permissions.SetApproveAllAsync(true);
                        if (!r.Success) EmitStatus("[Permission] SetApproveAllAsync returned Success=false.");
                    }
                    catch (Exception ex) { EmitStatus($"[Permission] SetApproveAllAsync failed: {ex.Message}"); }
                }
                EmitPendingStatus(session.SessionId);
                return;
            }
            catch { /* Session disk data also gone; fall through to a fresh session. */ }
        }

        await CreateMainSessionAsync();
    }

    private async Task CreateMainSessionAsync()
    {
        var agents    = LoadTierAgents();
        var skillDirs = LoadTierSkillDirectories();
        LoadTierPrompts();

        var session = await _client!.CreateSessionAsync(new SessionConfig
        {
            SessionId = GenerateSessionId(),
            Model = ActiveModel,
            ReasoningEffort = ActiveReasoningEffort,
            Streaming = true,
            OnPermissionRequest = BuildPermissionHandler(),
            OnUserInputRequest = BuildUserInputHandler(),
            SystemMessage = BuildSystemMessage(),
            CustomAgents     = agents.Count    > 0 ? agents    : null,
            SkillDirectories = skillDirs.Count > 0 ? skillDirs : null,
        });

        _mainSession = session;
        _sessions[session.SessionId] = session;
        session.On<SessionEvent>(evt => HandleSessionEvent(session.SessionId, evt));

        if (AutoApprove || ActiveMode == "Autopilot")
        {
            try
            {
                var r = await session.Rpc.Permissions.SetApproveAllAsync(true);
                if (!r.Success) EmitStatus("[Permission] SetApproveAllAsync returned Success=false.");
            }
            catch (Exception ex) { EmitStatus($"[Permission] SetApproveAllAsync failed: {ex.Message}"); }
        }

        if (FleetMode)
        {
            try
            {
#pragma warning disable GHCP001
                var fleetResult = await session.Rpc.Fleet.StartAsync(
                    prompt: WorkingDirectory != null ? BuildFleetScopeDirective() : null);
#pragma warning restore GHCP001
                if (!fleetResult.Started)
                    throw new InvalidOperationException(
                        "Fleet mode could not be activated. " +
                        "The server returned Started=false — Fleet may not be available for the selected model or account.");
            }
            catch
            {
                // Roll back the session so the next send retries cleanly
                _sessions.Remove(session.SessionId);
                _mainSession = null;
                try { await session.DisposeAsync(); } catch { }
                throw;
            }
        }

        SessionCreated?.Invoke(this, new SessionEventArgs
        {
            SessionId = session.SessionId,
            IsSubAgent = false,
        });
        EmitPendingStatus(session.SessionId);
    }

    private void EmitPendingStatus(string sessionId)
    {
        while (_pendingStatusMessages.Count > 0)
        {
            var msg = _pendingStatusMessages.Dequeue();
            MessageReceived?.Invoke(this, new SessionMessageEventArgs
            {
                SessionId = sessionId,
                Content   = msg,
                Kind      = MessageKind.Status,
            });
        }
    }

    private void EmitStatus(string message)
    {
        if (_mainSession?.SessionId is string sid && !string.IsNullOrEmpty(sid))
        {
            MessageReceived?.Invoke(this, new SessionMessageEventArgs
            {
                SessionId = sid,
                Content   = message,
                Kind      = MessageKind.Status,
            });
            return;
        }

        // If we don't have a session ID yet, queue the status to emit once the session is created.
        _pendingStatusMessages.Enqueue(message);
    }

    private static string FormatTokensShort(double tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000:0.#}M";
        if (tokens >= 1_000)     return $"{tokens / 1_000:0.#}K";
        return ((int)tokens).ToString();
    }

    private SystemMessageConfig? BuildSystemMessage()
    {
        var parts = new List<string>();

        // Scratchpad directive — always present when a working directory is set
        if (ScratchpadPath != null)
        {
            parts.Add(
                $"GOPILOT SCRATCHPAD: You have a dedicated scratch directory at \"{ScratchpadPath}\". " +
                "Write ALL temporary files here — SQLite databases, logs, intermediate outputs, diff backups, " +
                "and any other files you create during a task. " +
                "Never create temporary files in the project root or elsewhere.");
        }

        // Tool resilience directive — always present
        parts.Add(
            "TOOL RESILIENCE: If a built-in tool operation (create, edit, view, grep, glob) " +
            "fails or returns an \"interrupted\" error, immediately notify the user of the failure, " +
            "then retry the operation using equivalent PowerShell commands " +
            "(e.g., Set-Content, Add-Content, Get-Content, Get-ChildItem, Select-String). " +
            "Do not retry the same built-in tool that failed.");

        // Tiered instructions: Personal -> Skill Tree[*] -> Project
        var loadedTiers = new List<string>();
        foreach (var (label, folder) in GetTierFolders())
        {
            var instructionsPath = Path.Combine(folder, "gopilot-instructions.md");
            if (!File.Exists(instructionsPath)) continue;
            try
            {
                var instructions = File.ReadAllText(instructionsPath);
                if (!string.IsNullOrWhiteSpace(instructions))
                {
                    parts.Add($"{label} INSTRUCTIONS (from gopilot-instructions.md):\n\n{instructions.Trim()}");
                    loadedTiers.Add(label);
                }
            }
            catch { /* best-effort; skip if unreadable */ }
        }
        if (loadedTiers.Count > 0)
            _pendingStatusMessages.Enqueue($"Instructions loaded: {string.Join(", ", loadedTiers)}");

        // Mode-specific directive
        switch (ActiveMode)
        {
            case "Plan":
                parts.Add(
                    "PLAN MODE: Before taking any action, lay out a numbered step-by-step plan " +
                    "and wait for the user to confirm before executing. Always show your reasoning.");
                break;
            case "Autopilot":
                parts.Add(
                    "AUTOPILOT MODE: Work autonomously to complete the user's goal end-to-end. " +
                    "Use all available tools without asking for confirmation at each step. " +
                    "Summarise what you did when finished.");
                break;
        }

        // Fleet scope restriction
        if (FleetMode && WorkingDirectory != null)
        {
            parts.Add(BuildFleetScopeDirective());
        }

        // Caveman directive — instructs the model to also minimise its output
        // tokens. Baked in at session creation so it survives compaction; the
        // mid-session toggle in MainForm sends a separate one-shot instruction.
        if (CavemanMode)
        {
            parts.Add(
                "CAVEMAN MODE: Use caveman speak. Maximize information density. Fewest tokens. " +
                "Speak primitive. Use nouns and verbs. No grammar filler (the, is, are, of). " +
                "Keep words short. Save tokens. Be blunt. " +
                "Skip openers. Skip closures. Skip preambles. Skip filler transitions. " +
                "Apply this to all prose responses; preserve code, file paths, command syntax, " +
                "and tool output verbatim.");
        }

        if (parts.Count == 0) return null;

        return new SystemMessageConfig
        {
            Content = string.Join("\n\n", parts),
            Mode = SystemMessageMode.Append,
        };
    }

    private string BuildFleetScopeDirective() =>
        $"FLEET SCOPE RESTRICTION: You and every sub-agent you spawn are scoped strictly to the " +
        $"project root \"{WorkingDirectory}\". " +
        "Do NOT read, write, list, or access any path outside this root unless the user has " +
        "explicitly provided an absolute path to an external location in their message. " +
        "Treat all relative paths as relative to the project root. " +
        "If a task would require leaving the project root without explicit user permission, stop and ask.";

    // ── 3-Tier helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the environment dictionary to pass to the Copilot CLI process.
    /// Inherits the current process environment and sets
    /// <c>COPILOT_CUSTOM_INSTRUCTIONS_DIRS</c> so the CLI can discover
    /// <c>AGENTS.md</c> and <c>.github/instructions/**/*.instructions.md</c>
    /// files in the personal and Skill Tree tier folders.
    ///
    /// The project tier is already covered by the <c>Cwd</c> option; the CLI
    /// natively loads instructions from its working directory.  Personal
    /// <c>copilot-instructions.md</c> is loaded automatically from
    /// <c>~/.copilot</c> by the CLI, but <c>AGENTS.md</c> and
    /// <c>.github/instructions/</c> in that folder require an explicit entry
    /// in this env var.
    ///
    /// Any directories already present in the process-level
    /// <c>COPILOT_CUSTOM_INSTRUCTIONS_DIRS</c> are preserved after the
    /// GoPilot-managed tiers so that user-level customisation is not lost.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildCliEnvironment()
    {
        // Inherit the full process environment so the CLI continues to work.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
                env[k] = v;
        }

        // Build COPILOT_CUSTOM_INSTRUCTIONS_DIRS in Personal -> SkillTree[*] order.
        // Deduplication is case-insensitive (Windows paths).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();

        var personal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");
        if (Directory.Exists(personal) && seen.Add(personal))
            dirs.Add(personal);

        foreach (var folder in SkillTreeFolders)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            if (!Directory.Exists(folder)) continue;
            if (seen.Add(folder))
                dirs.Add(folder);
        }

        // Preserve any extra directories the user has configured in the env var.
        if (env.TryGetValue("COPILOT_CUSTOM_INSTRUCTIONS_DIRS", out var existing))
        {
            foreach (var d in existing.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(d))
                    dirs.Add(d);
            }
        }

        if (dirs.Count > 0)
            env["COPILOT_CUSTOM_INSTRUCTIONS_DIRS"] = string.Join(",", dirs);
        else
            env.Remove("COPILOT_CUSTOM_INSTRUCTIONS_DIRS");

        return env;
    }

    /// <summary>
    /// Searches PATH for a <c>copilot.exe</c> installation and returns its full path.
    /// Returns <c>null</c> when not found; the SDK will then fall back to its bundled CLI.
    /// </summary>
    private static string? ResolveCliFromPath()
    {
        var pathVar = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var trimmed = dir.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var candidate = Path.Combine(trimmed, "copilot.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Returns the Windows file version of the SDK's bundled <c>copilot.exe</c>, or
    /// <c>null</c> if the bundled binary cannot be located.
    /// </summary>
    private static Version? GetBundledCliFileVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "copilot.exe");
        return File.Exists(path) ? GetExeFileVersion(path) : null;
    }

    /// <summary>
    /// Returns the Windows file version of <paramref name="path"/>, following symlinks.
    /// Returns <c>null</c> on any failure.
    /// </summary>
    private static Version? GetExeFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (Version.TryParse(info.FileVersion, out var v)) return v;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Returns the active tier folders in Personal -> SkillTree[*] -> Project order.
    /// Only tiers whose root directory actually exists are included.  Each Skill Tree
    /// folder is yielded as a separate tier labelled <c>SKILL[i]</c> so that later
    /// entries override earlier ones for agent-name collisions.
    /// </summary>
    internal IEnumerable<(string Label, string Path)> GetTierFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var personal = Path.Combine(userProfile, ".copilot");
        if (!string.IsNullOrEmpty(personal) && Directory.Exists(personal) && seen.Add(personal))
            yield return ("PERSONAL", personal);

        // The user's default ~/.github folder is consulted automatically by the
        // Copilot CLI / SDK, so surface its agents/ and skills/ in our lists too.
        var personalGithub = Path.Combine(userProfile, ".github");
        if (!string.IsNullOrEmpty(personalGithub) && Directory.Exists(personalGithub) && seen.Add(personalGithub))
            yield return ("PERSONAL_GITHUB", personalGithub);

        int index = 1;
        foreach (var folder in SkillTreeFolders)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            if (!Directory.Exists(folder)) continue;
            if (!seen.Add(folder)) continue;
            yield return ($"SKILL[{index}]", folder);
            index++;
        }

        if (!string.IsNullOrEmpty(WorkingDirectory) && Directory.Exists(WorkingDirectory) && seen.Add(WorkingDirectory))
            yield return ("PROJECT", WorkingDirectory);

        // The project's .github folder is also consulted automatically by the
        // Copilot CLI / SDK, so include its agents/ and skills/ as well.
        if (!string.IsNullOrEmpty(WorkingDirectory))
        {
            var projectGithub = Path.Combine(WorkingDirectory, ".github");
            if (Directory.Exists(projectGithub) && seen.Add(projectGithub))
                yield return ("PROJECT_GITHUB", projectGithub);
        }
    }

    /// <summary>
    /// Discovers agent definition files (*.md) from the <c>agents/</c> subdirectory of each
    /// tier folder.  A later tier's definition silently overrides an earlier tier's agent with
    /// the same name (project beats later Skill Tree entries, which beat earlier ones, which beat personal).
    /// </summary>
    private List<CustomAgentConfig> LoadTierAgents()
    {
        var map      = new Dictionary<string, CustomAgentConfig>(StringComparer.OrdinalIgnoreCase);
        var infoMap  = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, folder) in GetTierFolders())
        {
            var agentsDir = System.IO.Path.Combine(folder, "agents");
            if (!Directory.Exists(agentsDir)) continue;

            foreach (var file in Directory.GetFiles(agentsDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var agent = ParseAgentFile(file);
                if (agent != null)
                {
                    map[agent.Name] = agent;
                    infoMap[agent.Name] = new AgentInfo
                    {
                        Name        = agent.Name,
                        DisplayName = agent.DisplayName,
                        Description = agent.Description,
                        FilePath    = file,
                        Tier        = label,
                    };
                }
            }
        }

        _cachedAgents = infoMap.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return [.. map.Values];
    }

    /// <summary>
    /// Returns all existing <c>skills/</c> subdirectories across the tier folders, in
    /// Personal -> SkillTree[*] -> Project order.
    /// </summary>
    private List<string> LoadTierSkillDirectories()
    {
        var dirs    = new List<string>();
        var skills  = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, folder) in GetTierFolders())
        {
            var skillsDir = System.IO.Path.Combine(folder, "skills");
            if (!Directory.Exists(skillsDir)) continue;

            dirs.Add(skillsDir);

            // Each direct subdirectory of skillsDir is a skill — look for SKILL.md.
            foreach (var sub in Directory.GetDirectories(skillsDir))
            {
                var skillFile = System.IO.Path.Combine(sub, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var info = ParseSkillFile(skillFile, label);
                if (info != null)
                    skills[info.Name] = info;   // later tier overrides earlier
            }
        }

        _cachedSkills = skills.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return dirs;
    }

    /// <summary>
    /// Discovers prompt template files (*.md) from the <c>prompts/</c> subdirectory
    /// of each tier folder.  Prompts are author-curated prompt bodies that the user
    /// attaches to outgoing messages exactly the same way a regular file attachment
    /// works -- there is no SDK-side concept of a "prompt", so we only need to know
    /// the file's name, optional description, and on-disk path so the picker dialog
    /// can offer it.  A later tier silently overrides an earlier tier's prompt with
    /// the same name (project beats Skill Tree, which beats personal).
    /// </summary>
    private void LoadTierPrompts()
    {
        var map = new Dictionary<string, PromptInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, folder) in GetTierFolders())
        {
            var promptsDir = System.IO.Path.Combine(folder, "prompts");
            if (!Directory.Exists(promptsDir)) continue;

            foreach (var file in Directory.GetFiles(promptsDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var info = ParsePromptFile(file, label);
                if (info != null)
                    map[info.Name] = info;   // later tier overrides earlier
            }
        }

        _cachedPrompts = map.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Parses a prompt template Markdown file. Supports the same optional YAML
    /// front matter convention used by agents/skills (<c>name</c>, <c>description</c>).
    /// The file stem is used as the prompt name when no front matter is present.
    /// Returns null when the file cannot be read.
    /// </summary>
    private static PromptInfo? ParsePromptFile(string filePath, string tier)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { return null; }

        content = content.Replace("\r\n", "\n");

        string? name        = null;
        string? description = null;

        if (content.StartsWith("---\n"))
        {
            var end = content.IndexOf("\n---\n", 4);
            if (end >= 0)
            {
                var frontMatter = content[4..end];
                foreach (var rawLine in frontMatter.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var col = line.IndexOf(':');
                    if (col < 0) continue;

                    var key = line[..col].Trim().ToLowerInvariant();
                    var val = line[(col + 1)..].Trim().Trim('"', '\'');

                    switch (key)
                    {
                        case "name":        name        = val; break;
                        case "description": description = val; break;
                    }
                }
            }
        }

        name ??= System.IO.Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new PromptInfo
        {
            Name        = name,
            Description = description,
            FilePath    = filePath,
            Tier        = tier,
        };
    }

    /// <summary>
    /// Parses an agent definition Markdown file.  Supports an optional YAML front matter
    /// block delimited by <c>---</c> lines that may supply <c>name</c>, <c>displayName</c>,
    /// <c>description</c>, and <c>tools</c>.  The file stem is used as the agent name when
    /// no front matter is present.  Returns null when the file cannot be read or has no
    /// usable prompt body.
    /// </summary>
    private static CustomAgentConfig? ParseAgentFile(string filePath)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { return null; }

        content = content.Replace("\r\n", "\n");

        string? name        = null;
        string? displayName = null;
        string? description = null;
        List<string>? tools = null;
        string prompt       = content;

        if (content.StartsWith("---\n"))
        {
            var end = content.IndexOf("\n---\n", 4);
            if (end >= 0)
            {
                var frontMatter = content[4..end];
                prompt = content[(end + 5)..].TrimStart();

                var inTools = false;
                tools = [];

                foreach (var rawLine in frontMatter.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.Equals("tools:", StringComparison.OrdinalIgnoreCase))
                    {
                        inTools = true;
                        continue;
                    }

                    if (inTools)
                    {
                        if (line.StartsWith("- "))
                        {
                            tools.Add(line[2..].Trim());
                            continue;
                        }
                        inTools = false;
                    }

                    var col = line.IndexOf(':');
                    if (col < 0) continue;

                    var key = line[..col].Trim().ToLowerInvariant();
                    var val = line[(col + 1)..].Trim().Trim('"', '\'');

                    switch (key)
                    {
                        case "name":        name        = val; break;
                        case "displayname": displayName = val; break;
                        case "description": description = val; break;
                    }
                }

                if (tools.Count == 0) tools = null;
            }
        }

        name ??= System.IO.Path.GetFileNameWithoutExtension(filePath);

        if (string.IsNullOrWhiteSpace(prompt)) return null;

        return new CustomAgentConfig
        {
            Name        = name,
            DisplayName = displayName,
            Description = description,
            Tools       = tools,
            Prompt      = prompt,
        };
    }

    /// <summary>
    /// Parses a <c>SKILL.md</c> file from a skills/{skill}/ subdirectory.  Reads
    /// the YAML front matter for <c>name</c> and <c>description</c>, then scans
    /// the body for the bullets under <c>## When to Use This Skill</c> to surface
    /// as "trigger words" in the UI.
    /// </summary>
    private static SkillInfo? ParseSkillFile(string filePath, string tier)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { return null; }

        content = content.Replace("\r\n", "\n");

        string? name        = null;
        string? description = null;
        string  body        = content;

        if (content.StartsWith("---\n"))
        {
            var end = content.IndexOf("\n---\n", 4);
            if (end >= 0)
            {
                var frontMatter = content[4..end];
                body = content[(end + 5)..];

                foreach (var rawLine in frontMatter.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var col = line.IndexOf(':');
                    if (col < 0) continue;

                    var key = line[..col].Trim().ToLowerInvariant();
                    var val = line[(col + 1)..].Trim().Trim('"', '\'');

                    switch (key)
                    {
                        case "name":        name        = val; break;
                        case "description": description = val; break;
                    }
                }
            }
        }

        // Fall back to the parent folder name (the skill directory) when no
        // explicit name was given.
        name ??= new DirectoryInfo(System.IO.Path.GetDirectoryName(filePath)!).Name;

        var triggers = ExtractTriggers(body);

        return new SkillInfo
        {
            Name        = name,
            Description = description,
            Triggers    = triggers,
            FolderPath  = System.IO.Path.GetDirectoryName(filePath)!,
            Tier        = tier,
        };
    }

    /// <summary>
    /// Pulls the bullet lines that immediately follow a "## When to Use This Skill"
    /// (or similar) heading.  Trims the leading "User asks to "/"User wants to "
    /// boilerplate so the column reads as keyword-like phrases.
    /// </summary>
    private static List<string> ExtractTriggers(string body)
    {
        var lines = body.Split('\n');
        var triggers = new List<string>();
        var inSection = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            if (line.StartsWith("## "))
            {
                var heading = line[3..].Trim().ToLowerInvariant();
                inSection = heading.Contains("when to use") || heading.Contains("triggers");
                continue;
            }

            if (!inSection) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#")) { inSection = false; continue; }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var item = trimmed[2..].Trim();
                // Strip common boilerplate so the row reads as a keyword phrase.
                foreach (var prefix in new[] { "User asks to ", "User wants to ", "User needs to ", "When ", "User " })
                {
                    if (item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        item = item[prefix.Length..];
                        break;
                    }
                }
                if (item.Length > 0)
                    triggers.Add(item);
            }
        }

        return triggers;
    }


    public async Task AbortAsync()
    {
        if (_mainSession != null)
            await _mainSession.AbortAsync();
    }

    public async Task ResetSessionAsync()
    {
        if (_mainSession != null)
        {
            await _mainSession.DisposeAsync();
            _sessions.Remove(_mainSession.SessionId);
            _mainSession = null;
        }
        _approvedKinds.Clear();
        _currentInputTokens = 0;
    }

    /// <summary>
    /// Asks the CLI to compact the current session in place, summarising history
    /// to free context-window headroom while preserving the session ID.
    /// Returns true on success; false if the SDK call throws (e.g. the experimental
    /// endpoint is unavailable for this server build).
    /// </summary>
    public async Task<bool> CompactSessionAsync()
    {
        if (_mainSession == null) return false;
        try
        {
#pragma warning disable GHCP001
            var result = await _mainSession.Rpc.History.CompactAsync();
#pragma warning restore GHCP001
            if (!result.Success) return false;

            // Optimistically zero the meter — the next AssistantUsageEvent will
            // restore the true value once the user sends another prompt.
            _currentInputTokens = 0;
            ContextUsageChanged?.Invoke(this, new ContextUsageEventArgs
            {
                SessionId       = _mainSession.SessionId,
                InputTokens     = 0,
                MaxPromptTokens = _currentMaxPromptTokens,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Tears down the current main session, opens a fresh one in the same workspace,
    /// and seeds it with <paramref name="summary"/> via <paramref name="seedPrompt"/>.
    /// The new session inherits the active model, mode, and Fleet setting.
    /// </summary>
    public async Task RestartSessionWithSummaryAsync(string summary, string seedPrompt)
    {
        await ResetSessionAsync();
        await EnsureSessionAsync();

        _currentInputTokens = 0;
        ContextUsageChanged?.Invoke(this, new ContextUsageEventArgs
        {
            SessionId       = _mainSession?.SessionId ?? "",
            InputTokens     = 0,
            MaxPromptTokens = _currentMaxPromptTokens,
        });

        var fullPrompt = seedPrompt + "\r\n\r\n" + summary;
        await _mainSession!.SendAsync(new MessageOptions { Prompt = fullPrompt });
    }

    /// <summary>
    /// Resets the client state so a new connection can be established
    /// (e.g. after DisposeAsync to reconnect with a different working directory).
    /// </summary>
    public void Reset()
    {
        StopKeepAlive();
        _lifecycleSubscription?.Dispose();
        _lifecycleSubscription = null;
        _lifecycleDeletedSubscription?.Dispose();
        _lifecycleDeletedSubscription = null;
        _sessions.Clear();
        _mainSession = null;
        _pendingToolNames.Clear();
        _approvedKinds.Clear();
        _client = null;
        _isConnected = false;
        ConnectionStateChanged?.Invoke(this, "Not connected");
    }

    private void HandleSessionEvent(string sessionId, SessionEvent evt)
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId = sessionId,
                    Content = delta.Data.DeltaContent ?? "",
                    Kind = MessageKind.AssistantDelta,
                });
                break;

            case AssistantMessageEvent msg:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId = sessionId,
                    Content = msg.Data.Content ?? "",
                    Kind = MessageKind.AssistantFinal,
                });
                break;

            case AssistantReasoningEvent reasoning:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId = sessionId,
                    Content = reasoning.Data.Content ?? "",
                    Kind = MessageKind.Reasoning,
                });
                break;

            case ToolExecutionStartEvent tool:
                var toolName = tool.Data.ToolName ?? "";
                var toolCallId = tool.Data.ToolCallId ?? "";
                if (!string.IsNullOrEmpty(toolCallId))
                    _toolCallToName[toolCallId] = toolName;
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId        = sessionId,
                    Content          = toolName,
                    Kind             = MessageKind.ToolStart,
                    ToolCallId       = toolCallId,
                    ToolArgSummary   = SummariseArguments(tool.Data.Arguments),
                });
                break;

            case ToolExecutionCompleteEvent tool:
                var completedId = tool.Data.ToolCallId ?? "";
                var completedName = _toolCallToName.TryGetValue(completedId, out var name) ? name : completedId;
                var resultSummary = tool.Data.Success
                    ? SummariseResult(tool.Data.Result?.Content)
                    : SummariseResult(tool.Data.Error?.Message);
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId         = sessionId,
                    Content           = completedName,
                    Kind              = MessageKind.ToolComplete,
                    ToolCallId        = completedId,
                    ToolSuccess       = tool.Data.Success,
                    ToolResultSummary = resultSummary,
                });
                break;

            case ToolExecutionProgressEvent prog:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId  = sessionId,
                    Content    = prog.Data.ProgressMessage ?? "",
                    Kind       = MessageKind.ToolProgress,
                    ToolCallId = prog.Data.ToolCallId,
                });
                break;

            case SubagentStartedEvent sa:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId           = sessionId,
                    Content             = sa.Data.AgentName ?? "",
                    Kind                = MessageKind.SubAgentStart,
                    ToolCallId          = sa.Data.ToolCallId,
                    SubAgentDisplayName = sa.Data.AgentDisplayName ?? "",
                    SubAgentDescription = sa.Data.AgentDescription ?? "",
                });
                break;

            case SubagentCompletedEvent sa:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId           = sessionId,
                    Content             = sa.Data.AgentName ?? "",
                    Kind                = MessageKind.SubAgentComplete,
                    ToolCallId          = sa.Data.ToolCallId,
                    SubAgentDisplayName = sa.Data.AgentDisplayName ?? "",
                    SubAgentModel       = sa.Data.Model,
                    SubAgentTotalCalls  = sa.Data.TotalToolCalls,
                    SubAgentTotalTokens = sa.Data.TotalTokens,
                    SubAgentDurationMs  = sa.Data.Duration?.TotalMilliseconds,
                });
                break;

            case SubagentFailedEvent sa:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId           = sessionId,
                    Content             = sa.Data.Error ?? "",
                    Kind                = MessageKind.SubAgentFailed,
                    ToolCallId          = sa.Data.ToolCallId,
                    SubAgentDisplayName = sa.Data.AgentDisplayName ?? "",
                    SubAgentModel       = sa.Data.Model,
                    SubAgentTotalCalls  = sa.Data.TotalToolCalls,
                    SubAgentTotalTokens = sa.Data.TotalTokens,
                    SubAgentDurationMs  = sa.Data.Duration?.TotalMilliseconds,
                });
                break;

            case SkillInvokedEvent skill:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId           = sessionId,
                    Content             = skill.Data.Name,
                    Kind                = MessageKind.SkillInvoked,
                    SubAgentDescription = skill.Data.Description,
                });
                break;

            case SessionCustomAgentsUpdatedEvent agents:
                if (agents.Data.Agents.Length == 0 && agents.Data.Errors.Length == 0) break;
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId = sessionId,
                    Content   = FormatCustomAgentsSummary(agents.Data),
                    Kind      = MessageKind.CustomAgentsUpdated,
                });
                break;

            case AssistantStreamingDeltaEvent stream:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId  = sessionId,
                    Kind       = MessageKind.BytesUpdate,
                    TotalBytes = stream.Data.TotalResponseSizeBytes,
                });
                break;

            case SessionIdleEvent:
                SessionIdleForSession?.Invoke(this, sessionId);
                break;

            case SessionErrorEvent error:
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId = sessionId,
                    Content = error.Data.Message ?? "Unknown error",
                    Kind = MessageKind.Error,
                });
                break;

            case CommandsChangedEvent cmds:
                // Cache SDK-registered slash commands for the main session only.
                if (sessionId == _mainSession?.SessionId)
                    _cachedSdkCommands = cmds.Data.Commands;
                break;

            case PermissionRequestedEvent:
                // PermissionRequestedEvent is an informational event only.  Responding to
                // it via HandlePendingPermissionRequestAsync while OnPermissionRequest is
                // also set causes double-responses for the same request, which confuses the
                // CLI and produces "Unhandled permission result kind: [object Object]".
                // The OnPermissionRequest callback (required by the SDK) is the sole
                // response path; do not call HandlePendingPermissionRequestAsync here.
                break;

            case AssistantUsageEvent usage:
                // Update the context-window meter from the main user-driven flow only:
                // ignore sub-agents (ParentToolCallId set) and non-user initiators
                // (e.g. "sub-agent", "mcp-sampling").  The SDK schema documents
                // Initiator as absent for user-initiated calls, but some models
                // (observed: claude-opus-4.7) emit the literal string "user"
                // instead, so we accept both.  Use an allow-list so any future
                // non-user initiator type stays correctly excluded.
                if (sessionId == _mainSession?.SessionId
                    && IsUserInitiatedUsage(usage.Data.Initiator)
                    && usage.Data.InputTokens.HasValue)
                {
                    _currentInputTokens = usage.Data.InputTokens.Value;
                    if (_currentMaxPromptTokens <= 0)
                        _currentMaxPromptTokens = LookupMaxPromptTokens(usage.Data.Model);
                    ContextUsageChanged?.Invoke(this, new ContextUsageEventArgs
                    {
                        SessionId       = sessionId ?? "",
                        InputTokens     = _currentInputTokens,
                        MaxPromptTokens = _currentMaxPromptTokens,
                    });
                }
                break;

            case SessionInfoEvent info
                when sessionId == _mainSession?.SessionId
                  && string.Equals(info.Data.InfoType, "context_window", StringComparison.OrdinalIgnoreCase):
                MessageReceived?.Invoke(this, new SessionMessageEventArgs
                {
                    SessionId = sessionId,
                    Content   = info.Data.Message ?? "",
                    Kind      = MessageKind.Status,
                });
                break;
        }
    }

    // ── Argument / result summarisation helpers ───────────────────────────────

    private static string FormatCustomAgentsSummary(SessionCustomAgentsUpdatedData data)
    {
        var invocable = data.Agents.Where(a => a.UserInvocable).Select(a => a.DisplayName).ToArray();
        var sb = new StringBuilder();
        if (invocable.Length > 0)
            sb.Append($"{invocable.Length} custom agent{(invocable.Length == 1 ? "" : "s")} loaded: {string.Join(", ", invocable)}");
        foreach (var err  in data.Errors)   sb.Append($"\r\n  ✗ {err}");
        foreach (var warn in data.Warnings) sb.Append($"\r\n  ⚠ {warn}");
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the most human-readable text from a tool's JSON arguments object.
    /// Tries common meaningful keys in priority order, falling back to the raw JSON.
    /// </summary>
    private static string? SummariseArguments(object? args)
    {
        if (args == null) return null;
        try
        {
            JsonElement el = args is JsonElement je ? je
                : JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement;

            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "command", "description", "prompt", "query",
                                             "path", "expression", "input", "content" })
                {
                    if (el.TryGetProperty(key, out var prop) &&
                        prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            return Truncate(val.Trim(), 80);
                    }
                }
            }
            return Truncate(el.GetRawText(), 80);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns a one-line summary of a tool result, with a line count prefix when
    /// the result spans multiple lines (e.g. "16 lines: first line of output…").
    /// </summary>
    private static string? SummariseResult(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var first = lines[0].Trim();
        return lines.Length > 1
            ? $"{lines.Length} lines: {Truncate(first, 80)}"
            : Truncate(first, 100);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildPermissionHandler()
    {
        return async (request, invocation) =>
        {
            try
            {
                // Dynamic auto-approve: delegate to the SDK's own ApproveAll handler so we
                // never touch PermissionDecision serialization for the hot path.
                if (AutoApprove || ActiveMode == "Autopilot")
                    return await PermissionHandler.ApproveAll(request, invocation);

                // Kind previously approved for this session via "Approve Similar"
                if (_approvedKinds.Contains(request.Kind ?? ""))
                    return await PermissionHandler.ApproveAll(request, invocation);

                string? toolName = request is PermissionRequestMcp mcp ? mcp.ToolName : null;
                string? fileName = request switch
                {
                    PermissionRequestWrite w => w.FileName,
                    PermissionRequestRead r => r.Path,
                    PermissionRequestUrl u => u.Url,
                    _ => null,
                };
                string? commandText = request is PermissionRequestShell shell ? shell.FullCommandText : null;

                var args = new PermissionEventArgs
                {
                    OperationKind = request.Kind ?? "",
                    ToolName = toolName,
                    FileName = fileName,
                    CommandText = commandText,
                };

                PermissionRequested?.Invoke(this, args);

                bool approved = await args.Decision.Task;

                if (approved && args.ApproveSimilar && !string.IsNullOrEmpty(args.OperationKind))
                    _approvedKinds.Add(args.OperationKind);

                if (approved)
                    return await PermissionHandler.ApproveAll(request, invocation);

                return PermissionDecision.Reject(null);
            }
            catch (Exception ex)
            {
                EmitStatus($"[Permission] Handler error ({request?.Kind ?? "?"}): {ex.Message}");
                return PermissionDecision.Reject(null);
            }
        };
    }

    private Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>> BuildUserInputHandler()
    {
        return async (request, invocation) =>
        {
            var args = new UserInputEventArgs
            {
                Question = request.Question ?? "",
                Choices = (IReadOnlyList<string>?)request.Choices,
                AllowFreeform = request.AllowFreeform ?? true,
            };

            UserInputRequested?.Invoke(this, args);

            string answer = await args.Answer.Task;
            return new UserInputResponse
            {
                Answer = answer,
                WasFreeform = true,
            };
        };
    }

    public async ValueTask DisposeAsync()
    {
        StopKeepAlive();
        _lifecycleSubscription?.Dispose();
        _lifecycleDeletedSubscription?.Dispose();

        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
        _mainSession = null;
        _isConnected = false;

        if (_client != null)
        {
            try { await _client.DisposeAsync(); }
            catch { /* best-effort cleanup */ }
            _client = null;
        }
    }
}
