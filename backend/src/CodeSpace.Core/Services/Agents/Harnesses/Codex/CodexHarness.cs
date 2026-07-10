using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Harnesses.Codex;

/// <summary>
/// Adapter for OpenAI's Codex CLI. Drives <c>codex exec --json …</c> (the non-interactive mode built
/// for scripts/CI) and normalizes its JSONL event stream into <see cref="AgentEvent"/>s.
///
/// <para><b>Fidelity note:</b> the CLI surface (<c>exec</c>, <c>--json</c>, <c>--model</c>, <c>--sandbox</c>)
/// is well-documented, so <see cref="BuildInvocation"/> is exact. The JSONL event <i>type</i> names are
/// version-dependent, so <see cref="ParseEvents"/> classifies by tolerant keyword match and maps anything
/// it can't place to <see cref="AgentEventKind.Warning"/> (surfaced, never dropped). The exact type→kind
/// table is calibrated against real <c>codex exec --json</c> output when execution is wired (B0.4); the
/// normalization shape tested here is the stable contract.</para>
/// </summary>
public sealed class CodexHarness : IAgentHarness, IModelCredentialProjector, IMcpHarnessDeclaration, IAgentSessionTranscript, ISingletonDependency
{
    public const string HarnessKind = "codex-cli";

    /// <summary>The config-home-relative file Codex reads MCP-server declarations from (a TOML <c>[mcp_servers.&lt;name&gt;]</c> table). Pinned by a test — the runner writes the run-scoped server here.</summary>
    public const string McpDeclarationFile = "config.toml";

    /// <summary>Air-gapped operators pin a private build via this env var (Rule 8). Renaming it breaks their pin — see the pin test.</summary>
    public const string VersionEnvVar = "CODESPACE_CODEX_CLI_VERSION";

    /// <summary>Air-gapped operators (and tests) repoint the Codex binary via this env var — an absolute path or a PATH name. Renaming it breaks their pin — see the pin test.</summary>
    public const string CommandEnvVar = "CODESPACE_CODEX_CLI_PATH";

    /// <summary>The env var Codex reads its model API key from (OpenAI + OpenAI-compatible providers). Pinned by a test (Rule 8) — the agent authenticates with whatever lands here.</summary>
    public const string ApiKeyEnvVar = "OPENAI_API_KEY";

    /// <summary>The env var the credential projection carries the base-URL override on. Codex 0.142.x IGNORES it as an env var (it routes through a config-file model-provider, not <c>OPENAI_BASE_URL</c>), so the harness reads it back here and re-injects it as a <c>-c</c> model-provider override — see <see cref="AppendModelProviderConfig"/>. Pinned by a test (Rule 8).</summary>
    public const string BaseUrlEnvVar = "OPENAI_BASE_URL";

    /// <summary>The model-provider id Codex routes a gateway through (injected via <c>-c</c> overrides). Arbitrary but stable — pinned by a test.</summary>
    public const string ModelProviderId = "codespace";

    /// <summary>Codex 0.142.x supports ONLY the <c>responses</c> wire protocol (it dropped <c>chat</c>), so a custom gateway must speak the OpenAI Responses API. Pinned by a test (Rule 8).</summary>
    public const string ModelProviderWireApi = "responses";

    /// <summary>
    /// Codex's config-home override (relocates <c>~/.codex</c>). The runner points it at a per-run isolated dir so
    /// an agent run reads ONLY the credentials we inject — never the operator's personal <c>~/.codex</c>, whose
    /// <c>config.toml</c> model-provider base-URL would otherwise override our injected gateway. Pinned by a test (Rule 8).
    /// </summary>
    public const string ConfigHomeEnvVar = "CODEX_HOME";

    /// <summary>
    /// The config-home-relative directory Codex's native loader scans for skills. <c>$CODEX_HOME/skills</c> is the
    /// backward-compatible path (Codex 0.142.2 also scans the canonical <c>$HOME/.agents/skills</c>, but that follows
    /// the OS home, which the sandbox only remaps when confined — <c>CODEX_HOME</c> is set on EVERY path, confined or
    /// not, so it's the reliable per-run-isolated target). Pinned by a test (Rule 8) so an ACCIDENTAL edit of this
    /// projection-root constant is a deliberate, visible change. (The pin guards the CONSTANT, not Codex's runtime
    /// behavior — a Codex version bump that DROPPED the backward-compat root would surface only in a real-codex skill
    /// E2E, not added in this slice, so re-verify this path on a Codex upgrade.)
    /// </summary>
    public const string SkillsRoot = "skills";

    /// <summary>
    /// The config-home-relative directory Codex persists session rollouts under — <c>$CODEX_HOME/sessions/&lt;date&gt;/rollout-&lt;ts&gt;-&lt;id&gt;.jsonl</c>,
    /// the transcript <c>codex exec resume</c> reads. Capture GLOBS here (the timestamp isn't known ahead of time);
    /// restore writes a deterministic <c>rollout-&lt;id&gt;.jsonl</c> here (verified against codex 0.142.2: resume scans
    /// <c>sessions/</c> at any depth and matches the id in the <c>rollout-…</c> filename — the original timestamp is not
    /// needed). Pinned by a test (Rule 8) so an accidental edit is a visible, deliberate change.
    /// </summary>
    public const string SessionsRoot = "sessions";

    /// <summary>The config-home-relative instruction file Codex reads as its persona/base guidance — <c>$CODEX_HOME/AGENTS.md</c>, merged with any workspace <c>AGENTS.md</c> (verified against codex 0.142.2). B1 writes the persona + operating contract here (Codex <c>exec</c> has no system-prompt flag). Pinned by a test (Rule 8).</summary>
    public const string AgentsFile = "AGENTS.md";

    /// <summary>The pinned Codex CLI version — MUST match <c>CODEX_CLI_VERSION</c> in <c>backend/Dockerfile.worker</c> (the single source of truth); a pin test fails if they drift.</summary>
    internal const string DefaultVersion = "0.142.2";

    private const string DefaultCommand = "codex";

    public string Kind => HarnessKind;

    public string Version => System.Environment.GetEnvironmentVariable(VersionEnvVar) is { Length: > 0 } v ? v : DefaultVersion;

    public IReadOnlyList<string> Models { get; } = new[] { "gpt-5.3-codex", "gpt-5.4", "gpt-5.4-codex" };

    public SandboxSpec BuildInvocation(AgentTask task)
    {
        // P3.2: a CONTINUE re-stage rewrites the `exec --json` seed to `exec resume <id> --json` so Codex picks up the
        // prior thread. The subcommand must follow `exec` directly; --model, the `-c` overrides (incl. the sandbox on
        // the resume path — see AppendSandbox), and the Goal positional follow. Null (a fresh run) → the plain seed,
        // argv byte-identical.
        var args = task.ResumeFromSessionId is { Length: > 0 } resumeThreadId
            ? new List<string> { "exec", "resume", resumeThreadId, "--json" }
            : new List<string> { "exec", "--json" };

        // task.Tools is intentionally NOT projected here: Codex has no global tool allow-list (it restricts via
        // --sandbox + per-MCP-server enabled_tools), so a Claude-Code-style tool list has no faithful Codex flag.
        // The list rides along in the task for harnesses that enforce it (Claude Code → --allowed-tools); Codex
        // bounds the agent through the sandbox mode below instead.

        // Omit --model when blank so Codex picks its own default (the Model=empty rule). Passing an empty
        // string would emit `--model ""`, which Codex rejects.
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            args.Add("--model");
            args.Add(task.Model);
        }

        AppendSandbox(args, task);

        // Point Codex at a custom gateway (when one was projected) BEFORE the prompt positional — Codex parses `-c`
        // overrides as flags, so they must precede the goal.
        AppendModelProviderConfig(args, task);
        AppendTelemetryConfig(args, task);

        // P3.3: Codex's default hook trust-review flow requires an interactive decision before a NON-managed command
        // hook may run — a freshly generated per-run hook has no persisted trust record, and there is no human at a
        // non-interactive `exec` run to answer the prompt. Bypass ONLY when WE actually wrote a hook (never
        // unconditionally — an operator-authored hooks.json in the target repo still goes through normal review).
        if (InLoopAcceptanceHook.AppliesTo(task)) args.Add("--dangerously-bypass-hook-trust");

        // B1: Codex exec has NO system-prompt flag, so the persona + operating contract ride an AGENTS.md in the config
        // home (see BuildConfigHomeFiles) — verified against codex 0.142.2 that it loads $CODEX_HOME/AGENTS.md (and merges
        // it with any workspace AGENTS.md). So the Goal positional stays the CLEAN task, never conflated with the persona.
        args.Add(task.Goal);

        return new SandboxSpec
        {
            Command = ResolveCommand(),
            Args = args,
            WorkingDirectory = task.WorkspaceDirectory,
            Environment = task.Environment,
            TimeoutSeconds = task.TimeoutSeconds,
            // Isolate Codex's config home per run so it ignores the operator's personal ~/.codex.
            ConfigHomeEnvVars = new[] { ConfigHomeEnvVar },
            // Project the persona's skills as SKILL.md files the runner writes under CODEX_HOME/skills/<slug>/;
            // Codex's native loader discovers them there (the same Agent-Skills format + SkillProjection as Claude —
            // only the root differs, which is why it's CODEX_HOME's, not CLAUDE_CONFIG_DIR's). On a CONTINUE the prior
            // session's rollout is restored alongside them under sessions/ (see BuildConfigHomeFiles).
            ConfigHomeFiles = BuildConfigHomeFiles(task),
            // The agent reaches the network only when its permissions allow it (the sandbox severs egress otherwise).
            AllowNetwork = task.Permissions.Network == AgentNetworkAccess.On,
        };
    }

    /// <summary>
    /// P3 (IAgentSessionTranscript) — CAPTURE-locate: Codex writes each session to
    /// <c>$CODEX_HOME/sessions/&lt;date&gt;/rollout-&lt;ts&gt;-&lt;id&gt;.jsonl</c>. The timestamp + date aren't known ahead of
    /// time, so — unlike Claude's computable path — the file is FOUND by globbing <paramref name="configHome"/>'s
    /// <see cref="SessionsRoot"/> for the id-bearing rollout the CLI wrote (cwd is irrelevant; Codex keys the session on
    /// the id, not the cwd). Returns the config-home-relative path (the executor security-clamps it), or null when there
    /// is no session id or no matching file (→ a continue cold-starts).
    /// </summary>
    public string? SessionTranscriptRelativePath(string configHome, string? workspaceDirectory, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        var sessionsDir = Path.Combine(configHome, SessionsRoot);
        if (!Directory.Exists(sessionsDir)) return null;

        // Skip symlinked entries + dirs during the walk (AttributesToSkip = ReparsePoint) so a hostile planted symlink is
        // never surfaced or traversed — the executor's clamp is the backstop, this is defense in depth.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };

        // Among the id-bearing rollouts pick the FRESHEST: codex appends the resumed turn IN PLACE (one file, verified vs
        // 0.142.2), but were a version ever to write a NEW rollout on resume, the newest carries the latest turn — never
        // the stale restored copy. A full session id (UUID) can't be a substring of another distinct id, so this is an exact match.
        var match = Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", options)
            .Where(f => Path.GetFileName(f).Contains(sessionId, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return match is null ? null : Path.GetRelativePath(configHome, match);
    }

    /// <summary>
    /// The config-home files the runner materializes: (1) B1 — <c>AGENTS.md</c> carrying the persona + the always-on
    /// operating contract (Codex's native instruction channel, since <c>exec</c> has no system-prompt flag; codex loads
    /// <c>$CODEX_HOME/AGENTS.md</c> and merges it with any workspace AGENTS.md — verified against 0.142.2), ALWAYS present;
    /// (2) the persona's projected skills; PLUS (3) — on a CONTINUE — the prior session's restored rollout at
    /// <c>sessions/rollout-&lt;sessionId&gt;.jsonl</c> where <c>codex exec resume</c> finds it (codex scans <c>sessions/</c>
    /// at any depth and matches the id in the <c>rollout-…</c> filename, so a deterministic id-named rollout suffices).
    /// </summary>
    private static IReadOnlyList<ConfigHomeFile> BuildConfigHomeFiles(AgentTask task)
    {
        var files = new List<ConfigHomeFile>(SkillProjection.ToConfigHomeFiles(task.Skills, SkillsRoot))
        {
            new() { RelativePath = AgentsFile, Content = AgentOperatingContract.Compose(task.SystemPrompt) },
        };

        // On a CONTINUE, restore the prior rollout so `codex exec resume` re-opens the thread.
        if (task.ResumeFromSessionId is { Length: > 0 } sessionId && task.RestoredTranscript is { Length: > 0 } transcript)
            files.Add(new ConfigHomeFile { RelativePath = $"{SessionsRoot}/rollout-{sessionId}.jsonl", Content = transcript });

        // P3.3: the in-loop acceptance Stop hook — same generated script Claude Code wires, plus Codex's OWN
        // hooks.json wrapper. BuildInvocation pairs this with --dangerously-bypass-hook-trust, since a freshly
        // generated per-run hook has no persisted trust decision and Codex's default trust-review flow would
        // otherwise block on an interactive prompt that never comes in a non-interactive `exec` run.
        if (InLoopAcceptanceHook.AppliesTo(task))
        {
            files.Add(new ConfigHomeFile
            {
                RelativePath = InLoopAcceptanceHook.ScriptRelativePath,
                Content = InLoopAcceptanceHook.BuildScript(task.Acceptance!.Command, InLoopAcceptanceHook.MaxBlocks),
                IsExecutable = true,
            });
            files.Add(new ConfigHomeFile { RelativePath = "hooks.json", Content = StopHookJson });
        }

        return files;
    }

    /// <summary>
    /// The <c>hooks.json</c> wiring the generated <see cref="InLoopAcceptanceHook.ScriptRelativePath"/> to Codex's
    /// <c>Stop</c> event (matcher is not honored for <c>Stop</c> — Codex's own docs list it explicitly, so it's
    /// omitted). References the script via <c>$CODEX_HOME</c> directly, matching Codex's own documented example
    /// (<c>python3 ~/.codex/hooks/session_start.py</c>) — the command string is itself shell-interpreted, so a
    /// bare <c>"$CODEX_HOME"/...</c> reference expands at hook-invocation time with no wrapper needed.
    /// </summary>
    private static readonly string StopHookJson =
        "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"\\\"$CODEX_HOME\\\"/" + InLoopAcceptanceHook.ScriptRelativePath + "\"}]}]}}";

    public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) return Array.Empty<AgentEvent>();

        JsonElement root;
        using (var doc = TryParse(line))
        {
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<AgentEvent>();
            root = doc.RootElement.Clone();
        }

        var type = ReadType(root);
        if (type is null) return Array.Empty<AgentEvent>();

        // Codex 0.142.x threaded schema: `turn.started` is pure lifecycle and `item.started` duplicates the terminal
        // `item.completed` (same item, in_progress) — suppress both (like ClaudeCodeHarness drops system/init lines) so
        // the transcript shows ONE self-describing row per item, not a flood of bare "turn.started"/"item.started" names.
        // No reader consumes their payload.
        if (type is TurnStartedType or ItemStartedType) return Array.Empty<AgentEvent>();

        // `item.completed` nests its readable payload under `item` (item.type + text/command/message/items) — read the
        // human line + kind from THERE, not the top level (where the old flat schema carried them). This is the fix for
        // a terminal that otherwise showed a wall of bare "item.completed" names. Classify by the type alone so even a
        // malformed line whose `item` is absent/non-object never falls back to the bare "item.completed" envelope name.
        if (type == ItemCompletedType)
        {
            var hasItem = root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object;
            return new[] { new AgentEvent { Kind = hasItem ? MapItemKind(ReadItemType(item)) : AgentEventKind.Warning, Text = hasItem ? ReadItemText(item) : "item", Data = root } };
        }

        // `turn.failed` carries the real reason nested under `error.message` — surface it as an Error so BuildResult
        // reports it (the top-level ReadText can't reach a nested field and would leave a bare "turn.failed").
        if (type == TurnFailedType)
            return new[] { new AgentEvent { Kind = AgentEventKind.Error, Text = ReadNestedString(root, "error", "message") ?? type, Data = root } };

        // `thread.started` (its Data carries the thread_id the session reader needs) and `turn.completed` (its Data
        // carries the token usage the usage reader needs) MUST keep being produced so those readers still find them;
        // give them a clean line instead of the bare lifecycle name.
        if (type == ThreadStartedType) return new[] { new AgentEvent { Kind = AgentEventKind.Started, Text = "Session started", Data = root } };
        if (type == TurnCompletedType) return new[] { new AgentEvent { Kind = AgentEventKind.Completed, Text = "Turn complete", Data = root } };

        // The older FLAT schema (still emitted by the fake CLI + pre-threaded Codex) carries content at the top level —
        // read it there. Codex emits one event per JSONL line, so a line maps to a single event.
        return new[] { new AgentEvent { Kind = MapKind(type), Text = ReadText(root, type), Data = root } };
    }

    private const string ThreadStartedType = "thread.started";
    private const string TurnStartedType = "turn.started";
    private const string TurnCompletedType = "turn.completed";
    private const string TurnFailedType = "turn.failed";
    private const string ItemStartedType = "item.started";
    private const string ItemCompletedType = "item.completed";

    public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary) ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        // D3b-i: cost-accounting figure — Codex emits a cumulative token_count event per turn, so the last
        // recognizable usage is the run total. Null when the stream carried none. Useful on failure too.
        var usage = AgentTokenUsageReader.TryRead(events);

        // P3.1a: capture the CLI thread id (Codex's thread.started event carries thread_id) — the handle a rerun
        // threads back as `codex exec resume <id>` to CONTINUE this conversation. Null when the stream carried none.
        var sessionId = AgentSessionIdReader.TryRead(events);

        // exitCode==0 only means the CLI process itself didn't crash — Codex can still emit turn.failed mid-run
        // (surfaced above as an Error event) while the wrapping process exits clean. Trusting the exit code alone
        // would silently report that failed turn as Succeeded.
        if (exitCode == 0 && !AgentTerminalOutcomeReader.ReportedFailure(events))
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage, SessionId = sessionId };

        // Prefer an explicit Error event, else the CLI's final message (on a non-zero exit that's the
        // failure reason — e.g. a provider 401), else the bare exit code — so the real reason reaches
        // AgentRun.error and the node failure instead of an opaque "codex exited with code 1".
        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"codex exited with code {Sandbox.SandboxExitCode.Describe(exitCode)}";

        var exitReason = exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId };
    }

    /// <summary>
    /// Codex drives OpenAI + any endpoint that speaks the OpenAI <b>Responses</b> API, via a base-URL override (a
    /// self-hosted gateway, OpenRouter / Ollama IFF they expose <c>/responses</c>). Codex 0.142.x dropped the
    /// chat-completions wire, so a custom endpoint that only does <c>/chat/completions</c> won't work — see
    /// <see cref="AppendModelProviderConfig"/>.
    /// </summary>
    public IReadOnlyList<string> SupportedProviders { get; } = new[] { "OpenAI", "OpenRouter", "Ollama", "Custom" };

    /// <summary>
    /// Project a resolved credential onto Codex's env: the api key under <see cref="ApiKeyEnvVar"/> (omitted for a
    /// keyless local provider) and any base-URL override under <see cref="BaseUrlEnvVar"/>. The key is read directly
    /// from the env; the base URL is NOT (Codex 0.142.x ignores <c>OPENAI_BASE_URL</c>) — it rides on the env only as
    /// the carrier <see cref="AppendModelProviderConfig"/> reads back to build the <c>-c</c> model-provider override.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential)
    {
        EnsureSupported(credential.Provider);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(credential.ApiKey)) env[ApiKeyEnvVar] = credential.ApiKey;
        if (!string.IsNullOrEmpty(credential.BaseUrl)) env[BaseUrlEnvVar] = credential.BaseUrl;

        return env;
    }

    private void EnsureSupported(string provider)
    {
        if (!SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{Kind} cannot authenticate to model provider '{provider}'.", nameof(provider));
    }

    /// <summary>
    /// Route Codex at a custom OpenAI-compatible gateway via inline <c>-c</c> config overrides. Codex 0.142.x ignores
    /// the <c>OPENAI_BASE_URL</c> env var (it picks the endpoint from a config-file model-provider, not env), so the
    /// base URL the credential projection carried on <see cref="BaseUrlEnvVar"/> is re-injected here as a model-provider
    /// over the <see cref="ModelProviderWireApi"/> wire, reading the key from <see cref="ApiKeyEnvVar"/> (set by
    /// <see cref="ProjectToEnv"/>). Emitted ONLY when a base URL was projected — plain OpenAI keeps Codex's built-in
    /// provider (api.openai.com). The base URL is non-secret, so the argv carries it safely; the key stays in env. It
    /// must be the full OpenAI-compatible base (e.g. <c>https://host/v1</c>) — Codex appends <c>/responses</c>.
    /// </summary>
    private static void AppendModelProviderConfig(List<string> args, AgentTask task)
    {
        if (!task.Environment.TryGetValue(BaseUrlEnvVar, out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl)) return;

        // Each override is a bare `key=value` argv element (exec, not a shell — no shell-escaping needed). Codex splits
        // on the FIRST `=`, so a query string in the URL is safe; values are assumed URL-safe (operator-set DB config),
        // never TOML-quoted. `wire_api` is pinned to `responses` — the only protocol Codex 0.142.x accepts. The provider
        // id reuses the MCP server name string coincidentally; the two live in independent TOML tables (no collision).
        void Override(string keyValue) { args.Add("-c"); args.Add(keyValue); }

        Override($"model_provider={ModelProviderId}");
        Override($"model_providers.{ModelProviderId}.name={ModelProviderId}");
        Override($"model_providers.{ModelProviderId}.base_url={EnsureOpenAiVersionPath(baseUrl)}");
        Override($"model_providers.{ModelProviderId}.wire_api={ModelProviderWireApi}");
        Override($"model_providers.{ModelProviderId}.env_key={ApiKeyEnvVar}");
    }

    /// <summary>
    /// Codex appends <c>/responses</c> to the provider <c>base_url</c>, so the base MUST carry the OpenAI version
    /// segment — a root URL would hit <c>host/responses</c> → 404. Ensure a trailing <c>/v1</c> IDEMPOTENTLY: append it
    /// only when the path doesn't already end in <c>/v1</c>, so an operator URL that already ends in <c>/v1</c> (or
    /// OpenRouter's <c>/api/v1</c>) is left as-is — never doubled to <c>/v1/v1</c>. A trailing slash is trimmed first.
    /// This encodes the standard OpenAI <c>/v1</c> convention; a gateway that serves Responses under a different path
    /// must store that full path. Claude's mirror (<c>ClaudeCodeHarness</c>) STRIPS a trailing <c>/v1</c>, so ONE
    /// operator base URL serves both harnesses.
    /// </summary>
    internal static string EnsureOpenAiVersionPath(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');

        return trimmed.EndsWith("/v1", StringComparison.Ordinal) ? trimmed : trimmed + "/v1";
    }

    /// <summary>
    /// On a deny-by-default (Allowlist) egress run, silence Codex's one non-auth-gated telemetry default — the Statsig
    /// OTEL metrics exporter (→ <c>ab.chatgpt.com</c>) — plus analytics, so a sealed run makes ZERO call off its pinned
    /// allowlist (model + git). Analytics is already inert for an api-key provider, but pinning both is explicit. A
    /// Full-egress run is unchanged — byte-identical to before.
    /// </summary>
    private static void AppendTelemetryConfig(List<string> args, AgentTask task)
    {
        if (task.Permissions.Egress != AgentEgressPolicy.Allowlist) return;

        args.Add("-c"); args.Add("otel.metrics_exporter=none");
        args.Add("-c"); args.Add("analytics.enabled=false");
    }

    /// <summary>Codex hosts an MCP server from an <c>[mcp_servers.&lt;name&gt;]</c> table in its config home's <c>config.toml</c>. The harness owns the format — it renders the TOML content with the run-scoped socket + token baked in; the runner just writes the bytes.</summary>
    public McpHarnessDeclaration BuildMcpDeclaration(McpDeclarationContext context) => new()
    {
        RelativeFileName = McpDeclarationFile,
        Content = McpDeclarationWriter.RenderCodexToml(context),
    };

    /// <summary>The Codex executable — the <see cref="CommandEnvVar"/> override (absolute path / PATH name) when set, else <c>codex</c> on PATH.</summary>
    private static string ResolveCommand() =>
        System.Environment.GetEnvironmentVariable(CommandEnvVar) is { Length: > 0 } path ? path : DefaultCommand;

    private static string SandboxMode(AgentPermissions permissions) =>
        permissions.WriteScope == AgentWriteScope.ReadOnly ? "read-only" : "workspace-write";

    /// <summary>
    /// Apply the sandbox confinement. On a plain <c>exec</c> run it's the <c>--sandbox &lt;mode&gt;</c> flag; on an
    /// <c>exec resume &lt;id&gt;</c> run it must instead ride as a <c>-c sandbox_mode=&lt;mode&gt;</c> config override —
    /// because <c>--sandbox</c> is an <c>exec</c>-only flag the <c>resume</c> subcommand REJECTS (clap "unexpected
    /// argument", exit 2, verified against the pinned codex 0.142.2). <c>-c</c> is accepted on both, and
    /// <c>sandbox_mode</c> is the recognized config key the flag maps to, so a resumed run keeps the same confinement.
    /// </summary>
    private static void AppendSandbox(List<string> args, AgentTask task)
    {
        var mode = SandboxMode(task.Permissions);

        if (task.ResumeFromSessionId is { Length: > 0 })
        {
            args.Add("-c");
            args.Add($"sandbox_mode={mode}");
            return;
        }

        args.Add("--sandbox");
        args.Add(mode);
    }

    private static JsonDocument? TryParse(string s)
    {
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return null; }
    }

    /// <summary>Read the event discriminator — top-level <c>type</c>, or nested <c>msg.type</c> (Codex has used both envelopes).</summary>
    private static string? ReadType(JsonElement root)
    {
        if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();

        if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("type", out var mt) && mt.ValueKind == JsonValueKind.String)
            return mt.GetString();

        return null;
    }

    /// <summary>Tolerant type → normalized kind. Specific checks precede generic ones; unknown → Warning so nothing is silently lost.</summary>
    private static AgentEventKind MapKind(string type)
    {
        var t = type.ToLowerInvariant();

        if (t.Contains("reason")) return AgentEventKind.Reasoning;
        if (t.Contains("plan")) return AgentEventKind.PlanUpdate;
        if (t.Contains("command") || t.Contains("exec")) return AgentEventKind.CommandExecuted;
        if (t.Contains("patch") || t.Contains("file") || t.Contains("apply")) return AgentEventKind.FileChanged;
        if (t.Contains("mcp") || t.Contains("tool") || t.Contains("function")) return AgentEventKind.ToolCall;
        if (t.Contains("test")) return AgentEventKind.TestOutput;
        if (t.Contains("error")) return AgentEventKind.Error;
        if (t.Contains("message") || t.Contains("assistant")) return AgentEventKind.AssistantMessage;
        if (t.Contains("complete") || t.Contains("finish") || t.Contains("done")) return AgentEventKind.Completed;

        return AgentEventKind.Warning;
    }

    /// <summary>Pull a human-readable line from the common message-bearing fields (top-level or under <c>msg</c>); fall back to the type.</summary>
    private static string ReadText(JsonElement root, string type)
    {
        foreach (var key in new[] { "message", "text", "content", "command", "path", "summary" })
        {
            if (TryReadString(root, key, out var direct)) return direct;

            if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object && TryReadString(msg, key, out var nested)) return nested;
        }

        return type;
    }

    /// <summary>The threaded item's own discriminator (<c>item.type</c>) — the codex 0.142.x kind vocabulary (agent_message / command_execution / todo_list / error / reasoning). Null when absent.</summary>
    private static string? ReadItemType(JsonElement item) =>
        item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    /// <summary>Map a threaded item's kind to the normalized event kind. Unknown → Warning so a new item type is never silently dropped, only unstyled.</summary>
    private static AgentEventKind MapItemKind(string? itemType) => itemType switch
    {
        "agent_message" => AgentEventKind.AssistantMessage,
        "reasoning" => AgentEventKind.Reasoning,
        "command_execution" => AgentEventKind.CommandExecuted,
        "todo_list" => AgentEventKind.PlanUpdate,
        "error" => AgentEventKind.Error,
        _ => AgentEventKind.Warning,
    };

    /// <summary>The readable line for a threaded item — the message/command/error/plan the item carries. A todo_list renders as checklist lines; every other kind takes its first non-empty content field, so an UNRECOGNIZED item still yields real text (its content or, last resort, its type), never a bare "item.completed".</summary>
    private static string ReadItemText(JsonElement item)
    {
        var itemType = ReadItemType(item);

        if (itemType == "todo_list") return RenderTodos(item);

        foreach (var key in new[] { "text", "message", "command", "aggregated_output" })
            if (TryReadString(item, key, out var v)) return v;

        // A RECOGNIZED content-bearing kind that carried an empty field renders as a blank line (the FE falls back to the
        // humanized kind), never the raw kind token; only a genuinely unknown item keeps its type as a self-describing hint.
        return itemType is "agent_message" or "reasoning" or "command_execution" or "error" ? "" : itemType ?? "item";
    }

    /// <summary>Render a codex todo_list item as checklist lines ("[x] done" / "[ ] pending") — its <c>items[].text</c> + <c>completed</c>. Falls back to the bare kind when the list is empty/malformed.</summary>
    private static string RenderTodos(JsonElement item)
    {
        if (!item.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return "todo_list";

        var lines = new List<string>();
        foreach (var todo in items.EnumerateArray())
        {
            if (!TryReadString(todo, "text", out var text)) continue;

            var done = todo.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True;
            lines.Add((done ? "[x] " : "[ ] ") + text);
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "todo_list";
    }

    /// <summary>Read a nested string (<c>parent.key</c>) — for a payload whose readable line lives one object down (e.g. turn.failed's <c>error.message</c>). Null when absent.</summary>
    private static string? ReadNestedString(JsonElement root, string parent, string key) =>
        root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && TryReadString(p, key, out var v) ? v : null;

    private static bool TryReadString(JsonElement obj, string key, out string value)
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? "";
            return value.Length > 0;
        }

        value = "";
        return false;
    }
}
