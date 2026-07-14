using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// Adapter for Anthropic's Claude Code CLI. Drives <c>claude --print --output-format stream-json --verbose …</c>
/// (the non-interactive "print" mode built for scripts/CI) and normalizes its stream-json events into
/// <see cref="AgentEvent"/>s. The second harness behind <see cref="IAgentHarness"/> — it proves the layer is
/// harness-agnostic (Codex was the first), and unlike Codex it natively speaks a tool allow-list
/// (<c>--allowed-tools</c>), so it's the honest home for projecting a persona's tools (a follow-up slice).
///
/// <para><b>Fidelity note:</b> the CLI surface (<c>--print</c>, <c>--output-format stream-json</c> which
/// REQUIRES <c>--verbose</c>, <c>--model</c>, <c>--permission-mode</c>) is verified against <c>claude</c> v2.1.x,
/// so <see cref="BuildInvocation"/> is exact. The stream-json event shapes (nested <c>message.content[]</c>
/// blocks) are classified TOLERANTLY by keyword — anything unplaceable maps to <see cref="AgentEventKind.Warning"/>
/// (surfaced, never dropped) and pure setup lines return null — so a CLI version bump degrades gracefully; the
/// normalization shape tested here is the stable contract, calibrated against real output when execution is wired.</para>
/// </summary>
public sealed class ClaudeCodeHarness : IAgentHarness, IModelCredentialProjector, IMcpHarnessDeclaration, IAgentSessionTranscript, ISingletonDependency
{
    public const string HarnessKind = "claude-code";

    /// <summary>The config-home-relative file Claude Code reads MCP-server declarations from (a JSON <c>mcpServers</c> map). Pinned by a test — the runner writes the run-scoped server here.</summary>
    public const string McpDeclarationFile = ".mcp.json";

    /// <summary>Air-gapped operators pin a private build via this env var (Rule 8). Renaming it breaks their pin — see the pin test.</summary>
    public const string VersionEnvVar = "CODESPACE_CLAUDE_CODE_VERSION";

    /// <summary>Air-gapped operators (and tests) repoint the Claude Code binary via this env var — an absolute path or a PATH name. Renaming it breaks their pin — see the pin test.</summary>
    public const string CommandEnvVar = "CODESPACE_CLAUDE_CODE_PATH";

    /// <summary>The env var Claude Code reads its Anthropic API key from (direct Anthropic). Pinned by a test (Rule 8).</summary>
    public const string ApiKeyEnvVar = "ANTHROPIC_API_KEY";

    /// <summary>The env var Claude Code reads a base-URL override from (a gateway / proxy / Bedrock-style endpoint). Pinned by a test (Rule 8).</summary>
    public const string BaseUrlEnvVar = "ANTHROPIC_BASE_URL";

    /// <summary>The env var Claude Code authenticates a gateway/proxy with (used instead of the api key when talking to a non-Anthropic endpoint). Pinned by a test (Rule 8).</summary>
    public const string AuthTokenEnvVar = "ANTHROPIC_AUTH_TOKEN";

    /// <summary>
    /// Claude Code's config-dir override. The runner points it at a per-run isolated dir so an agent run reads
    /// ONLY the credentials we inject — never the operator's personal <c>~/.claude</c> (settings, hooks, CLAUDE.md),
    /// whose <c>env.ANTHROPIC_BASE_URL</c> would otherwise override our injected gateway. Pinned by a test (Rule 8).
    /// </summary>
    public const string ConfigDirEnvVar = "CLAUDE_CONFIG_DIR";

    /// <summary>The config-home-relative directory Claude Code's native loader scans for personal skills (<c>&lt;CLAUDE_CONFIG_DIR&gt;/skills/&lt;slug&gt;/SKILL.md</c>) — where the runner materializes the persona's projected skills.</summary>
    public const string SkillsRoot = "skills";

    /// <summary>
    /// Claude Code's "disable non-essential network traffic" switch — set to <c>1</c> for an Allowlist (deny-by-default)
    /// egress run so the CLI does NOT reach telemetry / feature-gating hosts (e.g. <c>statsig.anthropic.com</c>) that the
    /// egress allowlist (model + git only) does not pin. Without it, a deny-by-default run can stall on an unreachable
    /// telemetry endpoint (the C3 watchdog would eventually flag it NeedsReview — never the intent). Pinned by a test (Rule 8).
    /// </summary>
    public const string DisableNonEssentialTrafficEnvVar = "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC";

    /// <summary>Claude Code's "small/fast" background model — used for title/summary generation, <c>/compact</c>, lightweight steps. Defaults to a haiku model NAME. Pinned by a test (Rule 8).</summary>
    public const string SmallFastModelEnvVar = "ANTHROPIC_SMALL_FAST_MODEL";

    /// <summary>The model the <c>haiku</c> / <c>sonnet</c> / <c>opus</c> tier aliases resolve to. Pinned by a test (Rule 8).</summary>
    public const string DefaultHaikuModelEnvVar = "ANTHROPIC_DEFAULT_HAIKU_MODEL";
    public const string DefaultSonnetModelEnvVar = "ANTHROPIC_DEFAULT_SONNET_MODEL";
    public const string DefaultOpusModelEnvVar = "ANTHROPIC_DEFAULT_OPUS_MODEL";

    /// <summary>The model Claude Code spawns subagents on. Pinned by a test (Rule 8).</summary>
    public const string SubagentModelEnvVar = "CLAUDE_CODE_SUBAGENT_MODEL";

    /// <summary>Every env var Claude Code resolves a NON-primary model from — pinned to the run's model on a gateway so it never reaches for a default Anthropic model the gateway doesn't host (see <see cref="AddGatewayModelTiers"/>).</summary>
    private static readonly string[] BackgroundModelEnvVars = { SmallFastModelEnvVar, DefaultHaikuModelEnvVar, DefaultSonnetModelEnvVar, DefaultOpusModelEnvVar, SubagentModelEnvVar };

    /// <summary>Claude Code settings key that suppresses the WebFetch hostname PREFLIGHT to <c>api.anthropic.com</c> — the one inference-adjacent call NOT covered by <see cref="DisableNonEssentialTrafficEnvVar"/>. Delivered via <c>--settings</c> on a sealed-egress run. Pinned by a test (Rule 8).</summary>
    public const string SkipWebFetchPreflightSetting = "skipWebFetchPreflight";

    private const string AnthropicProvider = "Anthropic";

    /// <summary>The pinned Claude Code CLI version — MUST match <c>CLAUDE_CODE_VERSION</c> in <c>backend/Dockerfile.worker</c> (the single source of truth); a pin test fails if they drift.</summary>
    internal const string DefaultVersion = "2.1.193";

    private const string DefaultCommand = "claude";

    public string Kind => HarnessKind;

    public string Version => System.Environment.GetEnvironmentVariable(VersionEnvVar) is { Length: > 0 } v ? v : DefaultVersion;

    public IReadOnlyList<string> Models { get; } = new[] { "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5" };

    /// <summary>
    /// P3 (IAgentSessionTranscript): Claude persists each session at <c>projects/&lt;sanitized-cwd&gt;/&lt;id&gt;.jsonl</c>
    /// under <c>CLAUDE_CONFIG_DIR</c> — the file <c>--resume</c> reads. The path is COMPUTABLE from the cwd + id, so
    /// <paramref name="configHome"/> is unused (no search needed). The executor reads it from here to capture for a
    /// later CONTINUE (the same path <see cref="BuildConfigHomeFiles"/> RESTORES a transcript to). Null when there's no
    /// resolved cwd or session id to address it.
    /// </summary>
    public string? SessionTranscriptRelativePath(string configHome, string? workspaceDirectory, string? sessionId) =>
        string.IsNullOrWhiteSpace(workspaceDirectory) || string.IsNullOrEmpty(sessionId) ? null : ClaudeTranscriptPath.For(workspaceDirectory, sessionId);

    public SandboxSpec BuildInvocation(AgentTask task)
    {
        // --output-format stream-json REQUIRES --verbose in --print mode (the CLI rejects it otherwise).
        var args = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };

        // P3.2: a CONTINUE re-stage threads the prior session id as `--resume <id>` to pick up the conversation.
        // Placed right after the seed — before the variadic --allowed-tools / --permission-mode — so the trailing
        // positional Goal is never swallowed. Null (a fresh run) → omitted, argv byte-identical.
        if (task.ResumeFromSessionId is { Length: > 0 } resumeSessionId)
        {
            args.Add("--resume");
            args.Add(resumeSessionId);
        }

        // B1: the persona + the always-on operating contract, projected through Claude's NATIVE system-prompt channel
        // (--append-system-prompt), NOT prepended to the goal — Anthropic's guidance is a system-prompt persona outweighs
        // the same text in the user message. No persona ⇒ just the contract, argv byte-identical to a pre-persona run.
        args.Add("--append-system-prompt");
        args.Add(AgentOperatingContract.Compose(task.SystemPrompt));

        // B1 config isolation (CORRECTED — the earlier "print is hermetic, skills need --setting-sources" rationale was
        // wrong: that hermetic default is the programmatic Agent SDK's, NOT the `claude` CLI's). The CLI `claude -p`
        // AUTO-DISCOVERS + loads personal skills from CLAUDE_CONFIG_DIR/skills/<slug>/SKILL.md by DEFAULT — official docs:
        // headless.md "Without --bare, `claude -p` loads the same context an interactive session would, including
        // anything configured in ... ~/.claude"; cli-reference `--bare` = "skip auto-discovery of ... skills". So our
        // projected skills load with NO extra flag; the REAL requirement is that we NEVER pass --bare / --safe-mode
        // (guarded by a unit test). `--setting-sources` (user|project|local) instead controls which settings.json LAYERS
        // load (sdk-headless "To restrict which sources load, set settingSources"), NOT skill discovery. We pass
        // `--setting-sources user` when we project skills to PIN settings to the isolated per-run user config home (§344)
        // — so the run inherits ONLY our config, never the TARGET REPO's `.claude` project/local settings (an untrusted
        // -input vector). Byte-identical argv for a skill-less run. The real-model E2E is the live arbiter of application.
        // P3.3: the SAME pin is required when we project an in-loop acceptance Stop hook (BuildConfigHomeFiles writes a
        // settings.json) — without it, Claude ALSO loads the target repo's own project/local .claude settings, which
        // could carry an UNTRUSTED Stop hook of the repo's own. Widening this condition (not adding a second flag) keeps
        // the pin's rule uniform: whenever WE write settings into the isolated config home, that's the ONLY layer that loads.
        if (task.Skills is { Count: > 0 } || InLoopAcceptanceHook.AppliesTo(task))
        {
            args.Add("--setting-sources");
            args.Add("user");
        }

        AppendSealedEgressSettings(args, task);

        // Omit --model when blank so the CLI picks its own default (the Model=empty rule).
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            args.Add("--model");
            args.Add(task.Model);
        }

        // Project the tool allow-list. Placed BEFORE --permission-mode so the variadic stops at that flag and
        // the trailing positional prompt is never swallowed. null/empty → omit (the harness's default toolset).
        if (task.Tools is { Count: > 0 } tools)
        {
            args.Add("--allowed-tools");
            args.AddRange(tools);
        }

        args.Add("--permission-mode");
        args.Add(PermissionMode(task.Permissions));

        args.Add(task.Goal);   // the prompt is the trailing positional argument

        return new SandboxSpec
        {
            Command = ResolveCommand(),
            Args = args,
            WorkingDirectory = task.WorkspaceDirectory,
            Environment = BuildEnvironment(task),
            TimeoutSeconds = task.TimeoutSeconds,
            // Isolate Claude Code's config dir per run so it ignores the operator's personal ~/.claude.
            ConfigHomeEnvVars = new[] { ConfigDirEnvVar },
            // Project the persona's skills as SKILL.md files the runner writes under CLAUDE_CONFIG_DIR/skills/<slug>/;
            // Claude Code's native loader discovers them there (personal scope) and does the progressive disclosure.
            // On a CONTINUE the prior session's transcript is restored alongside them (see BuildConfigHomeFiles).
            ConfigHomeFiles = BuildConfigHomeFiles(task),
            // The agent reaches the network only when its permissions allow it (the sandbox severs egress otherwise).
            AllowNetwork = task.Permissions.Network == AgentNetworkAccess.On,
        };
    }

    /// <summary>
    /// The config-home files the runner materializes: the persona's projected skills, PLUS — on a CONTINUE — the prior
    /// session's restored transcript at <c>projects/&lt;sanitized-cwd&gt;/&lt;sessionId&gt;.jsonl</c> where
    /// <c>claude --resume</c> reads it, PLUS — when the task carries a real acceptance contract — the P3.3 in-loop
    /// Stop hook (the generated script + a <c>settings.json</c> wiring it to <c>hooks.Stop</c>). Each addition is
    /// purely additive and independently gated, so a run using none of them returns the bare skills list unchanged
    /// (byte-identical). The transcript-restore cwd encoding is the SHARPEST hazard (see
    /// <see cref="ClaudeTranscriptPath"/>): it must be the resolved cwd the process runs in, which the producer
    /// slice supplies.
    /// </summary>
    private static IReadOnlyList<ConfigHomeFile> BuildConfigHomeFiles(AgentTask task)
    {
        var files = SkillProjection.ToConfigHomeFiles(task.Skills, SkillsRoot).ToList();

        if (task.ResumeFromSessionId is { Length: > 0 } sessionId
            && task.RestoredTranscript is { Length: > 0 } transcript
            && !string.IsNullOrWhiteSpace(task.WorkspaceDirectory))
        {
            files.Add(new ConfigHomeFile
            {
                RelativePath = ClaudeTranscriptPath.For(task.WorkspaceDirectory, sessionId),
                Content = transcript,
            });
        }

        if (InLoopAcceptanceHook.AppliesTo(task))
        {
            files.Add(new ConfigHomeFile
            {
                RelativePath = InLoopAcceptanceHook.ScriptRelativePath,
                Content = InLoopAcceptanceHook.BuildScript(task.Acceptance!.Command, InLoopAcceptanceHook.MaxBlocks),
                IsExecutable = true,
            });
            files.Add(new ConfigHomeFile { RelativePath = "settings.json", Content = StopHookSettingsJson });
        }

        return files;
    }

    /// <summary>
    /// The <c>settings.json</c> wiring the generated <see cref="InLoopAcceptanceHook.ScriptRelativePath"/> to Claude
    /// Code's <c>Stop</c> hook. References the script via the <c>$CLAUDE_CONFIG_DIR</c> env var (never a baked-in
    /// absolute path) because <see cref="BuildInvocation"/> runs BEFORE the runner assigns the actual per-run
    /// config-home directory — the env var is resolved at hook-invocation time, once the real path is known. The
    /// explicit <c>"shell":"bash"</c> handler field (documented for command hooks) forces the WHOLE command string
    /// through a real shell, so the <c>$CLAUDE_CONFIG_DIR</c> reference expands regardless of how the surrounding
    /// hook-handler value would otherwise be tokenized — no nested quoting to get wrong.
    /// </summary>
    private static readonly string StopHookSettingsJson =
        "{\"hooks\":{\"Stop\":[{\"matcher\":\"\",\"hooks\":[{\"type\":\"command\",\"command\":\"\\\"$CLAUDE_CONFIG_DIR\\\"/" + InLoopAcceptanceHook.ScriptRelativePath + "\",\"shell\":\"bash\"}]}]}}";

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

        var type = ReadString(root, "type");
        if (type.Length == 0) return Array.Empty<AgentEvent>();

        if (type.Contains("result", StringComparison.OrdinalIgnoreCase))
            return new[] { new AgentEvent { Kind = IsErrorResult(root) ? AgentEventKind.Error : AgentEventKind.Completed, Text = ReadResultText(root), Data = root } };

        if (type is "assistant" or "user")
            return ReadContentEvents(root);

        // The "init" line is the FIRST reliable carrier of the CLI's session_id (confirmed against real claude
        // v2.1.x stream-json output) — surface it as a minimal lifecycle event so AgentSessionIdReader finds it
        // even when the run is killed before reaching its terminal "result" line (AgentRunExecutor's TimedOut /
        // Stalled forced-terminal paths), mirroring exactly why CodexHarness keeps its thread.started line. Every
        // OTHER system subtype (hook lifecycle noise) still carries no run step and stays dropped, unchanged.
        if (type is "system")
            return ReadString(root, "subtype") == "init"
                ? new[] { new AgentEvent { Kind = AgentEventKind.Started, Text = "Session started", Data = root } }
                : Array.Empty<AgentEvent>();

        return new[] { new AgentEvent { Kind = AgentEventKind.Warning, Text = type, Data = root } };   // unknown → surfaced, never dropped
    }

    public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary)
                       ?? events.LastOrDefault(e => e.Kind == AgentEventKind.Completed)
                       ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        // D3b-i: cost-accounting figure — Claude's final result line carries a usage object; the reader
        // tolerantly extracts input/output tokens from it. Null when the stream carried none. On failure too.
        var usage = AgentTokenUsageReader.TryRead(events);

        // P3.1a: capture the CLI session id (Claude's result line carries session_id) — the handle a rerun
        // threads back as `claude --resume <id>` to CONTINUE this conversation. Null when the stream carried none.
        var sessionId = AgentSessionIdReader.TryRead(events);
        var model = AgentModelReader.TryRead(events);

        // exitCode==0 only means the CLI process itself didn't crash — Claude Code's own result line can still
        // carry is_error:true (e.g. a gateway 429 mid-turn), which IsErrorResult already normalizes into an
        // Error event above. Trusting the exit code alone would silently report that failed turn as Succeeded.
        if (exitCode == 0 && !AgentTerminalOutcomeReader.ReportedFailure(events))
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage, SessionId = sessionId, Model = model };

        // Surface the most actionable text we have: an explicit Error event, else the CLI's final
        // message (on a non-zero exit that's the failure reason — e.g. a provider 401), else the bare
        // exit code. Folding the summary in here means it reaches AgentRun.error and the node's failure
        // message, instead of the run failing with an opaque "claude exited with code 1".
        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"claude exited with code {Sandbox.SandboxExitCode.Describe(exitCode)}";

        var exitReason = exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId, Model = model };
    }

    /// <summary>Direct Anthropic, or any Anthropic-compatible gateway/proxy via a base-URL + auth-token override ("Custom").</summary>
    public IReadOnlyList<string> SupportedProviders { get; } = new[] { AnthropicProvider, "Custom" };

    /// <summary>
    /// Project a resolved credential onto Claude Code's env. Direct Anthropic uses <see cref="ApiKeyEnvVar"/>; a
    /// gateway/proxy ("Custom") authenticates with <see cref="AuthTokenEnvVar"/> + <see cref="BaseUrlEnvVar"/>
    /// (Claude Code reads the api key only for the official endpoint, the auth-token for everything else). The
    /// resolved key fills whichever applies; a base URL is added when present.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential)
    {
        EnsureSupported(credential.Provider);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        var isGateway = !string.Equals(credential.Provider, AnthropicProvider, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(credential.ApiKey)) env[isGateway ? AuthTokenEnvVar : ApiKeyEnvVar] = credential.ApiKey;
        if (!string.IsNullOrWhiteSpace(credential.BaseUrl)) env[BaseUrlEnvVar] = StripVersionSuffix(credential.BaseUrl);

        return env;
    }

    /// <summary>
    /// Claude Code's SDK appends <c>/v1/messages</c> to <see cref="BaseUrlEnvVar"/>, so the base must be the ROOT — a
    /// trailing <c>/v1</c> would produce <c>host/v1/v1/messages</c> → 404. Strip ONE trailing <c>/v1</c> (and any trailing
    /// slash) so an operator who entered the OpenAI-style <c>host/v1</c> form (or shares one base URL with the Codex
    /// harness, which REQUIRES <c>/v1</c> — see <c>CodexHarness.EnsureOpenAiVersionPath</c>) still connects. Idempotent:
    /// a root URL is returned unchanged.
    /// </summary>
    internal static string StripVersionSuffix(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');

        return trimmed.EndsWith("/v1", StringComparison.Ordinal) ? trimmed[..^3] : trimmed;
    }

    private void EnsureSupported(string provider)
    {
        if (!SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{Kind} cannot authenticate to model provider '{provider}'.", nameof(provider));
    }

    /// <summary>Claude Code hosts an MCP server from a <c>.mcp.json</c> in its config dir (a JSON <c>mcpServers</c> map). The harness owns the format — it renders the JSON content with the run-scoped socket + token baked in; the runner just writes the bytes.</summary>
    public McpHarnessDeclaration BuildMcpDeclaration(McpDeclarationContext context) => new()
    {
        RelativeFileName = McpDeclarationFile,
        Content = McpDeclarationWriter.RenderClaudeJson(context),
    };

    /// <summary>The Claude Code executable — the <see cref="CommandEnvVar"/> override (absolute path / PATH name) when set, else <c>claude</c> on PATH.</summary>
    private static string ResolveCommand() =>
        System.Environment.GetEnvironmentVariable(CommandEnvVar) is { Length: > 0 } path ? path : DefaultCommand;

    /// <summary>ReadOnly → plan (analysis, no edits); Workspace → bypassPermissions (autonomous within the OS sandbox, the Codex workspace-write analogue). The Autonomy dial refines this mapping later.</summary>
    private static string PermissionMode(AgentPermissions permissions) =>
        permissions.WriteScope == AgentWriteScope.ReadOnly ? "plan" : "bypassPermissions";

    /// <summary>
    /// The child env: the task's env, plus harness-injected entries — the <see cref="DisableNonEssentialTrafficEnvVar"/>
    /// for an Allowlist (deny-by-default) egress run (so the CLI doesn't stall reaching telemetry hosts the allowlist
    /// doesn't pin, B3.3c), and the gateway model-tier pins (<see cref="AddGatewayModelTiers"/>). An explicit
    /// <see cref="AgentTask.Environment"/> entry WINS (operator intent — layered last), matching the runner's
    /// NonInteractiveEnv "operator value wins" convention. When nothing is injected the task env is returned unchanged → byte-identical.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment(AgentTask task)
    {
        var injected = new Dictionary<string, string>(StringComparer.Ordinal);

        if (task.Permissions.Egress == AgentEgressPolicy.Allowlist) injected[DisableNonEssentialTrafficEnvVar] = "1";

        AddGatewayModelTiers(injected, task);

        if (injected.Count == 0) return task.Environment;

        foreach (var (key, value) in task.Environment) injected[key] = value;

        return injected;
    }

    /// <summary>
    /// On a NON-Anthropic gateway (the "Custom" provider — signalled by the projected <see cref="AuthTokenEnvVar"/>, which
    /// <see cref="ProjectToEnv"/> sets ONLY for a gateway), Claude Code still resolves its small/fast background model and
    /// its haiku/sonnet/opus tier aliases + subagents to DEFAULT Anthropic model NAMES — which a gateway that hosts only
    /// its own model family doesn't serve, so a run fails the moment Claude reaches for haiku (title/summary, <c>/compact</c>,
    /// a lightweight subagent). Pin EVERY non-primary tier to the run's own model so no unsupported name escapes. Only when
    /// a gateway auth-token was projected AND a model is set. Direct Anthropic — and an Anthropic-provider PROXY (api-key +
    /// base URL, which fronts real haiku) — are untouched, since neither carries the auth-token. The operator still wins
    /// (these are layered before the task env in <see cref="BuildEnvironment"/>).
    /// </summary>
    private static void AddGatewayModelTiers(Dictionary<string, string> env, AgentTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Model)) return;
        if (!task.Environment.TryGetValue(AuthTokenEnvVar, out var token) || string.IsNullOrWhiteSpace(token)) return;

        foreach (var key in BackgroundModelEnvVars) env[key] = task.Model;
    }

    /// <summary>
    /// On a deny-by-default (Allowlist) egress run, deliver <c>--settings {"<see cref="SkipWebFetchPreflightSetting"/>":true}</c>
    /// so a WebFetch tool call doesn't preflight the hostname against <c>api.anthropic.com</c> — a host the egress allowlist
    /// (model + git only) doesn't pin, which would stall the run (it's NOT covered by <see cref="DisableNonEssentialTrafficEnvVar"/>,
    /// the only other escape our env closes). Safe to add unconditionally: the runner writes NO <c>settings.json</c> into the
    /// per-run config dir (only <c>.mcp.json</c>, loaded independently), so <c>--settings</c> cannot clobber any run settings.
    /// A Full-egress run is unchanged.
    /// </summary>
    private static void AppendSealedEgressSettings(List<string> args, AgentTask task)
    {
        if (task.Permissions.Egress != AgentEgressPolicy.Allowlist) return;

        args.Add("--settings");
        args.Add($"{{\"{SkipWebFetchPreflightSetting}\":true}}");
    }

    /// <summary>
    /// Map EVERY content block of an assistant/user turn to its own event, in stream order — text → AssistantMessage,
    /// thinking → Reasoning, tool_use → command/file/tool, tool_result → command output. A single turn routinely
    /// carries several blocks (reasoning, then a tool_use, then text); emitting them all is what makes the durable
    /// log a faithful replay instead of a first-block-only summary. Each event keeps its OWN block as Data (a large
    /// reasoning / tool_result payload is offloaded downstream), so the row stays bounded.
    /// </summary>
    private static IReadOnlyList<AgentEvent> ReadContentEvents(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return Array.Empty<AgentEvent>();
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return Array.Empty<AgentEvent>();

        var events = new List<AgentEvent>();

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;

            var blockType = ReadString(block, "type");

            if (blockType == "text" && ReadString(block, "text") is { Length: > 0 } text)
                events.Add(new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = text, Data = block.Clone() });
            else if (blockType == "thinking" && ReadThinkingText(block) is { Length: > 0 } thinking)
                events.Add(new AgentEvent { Kind = AgentEventKind.Reasoning, Text = thinking, Data = block.Clone() });
            else if (blockType == "tool_use")
                events.Add(new AgentEvent { Kind = ClassifyTool(ReadString(block, "name")), Text = ToolText(block), Data = block.Clone() });
            else if (blockType == "tool_result" && ReadResultBlockText(block) is { Length: > 0 } resultText)
                events.Add(new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = resultText, Data = block.Clone() });
        }

        return events;
    }

    /// <summary>A Claude <c>thinking</c> block carries its raw reasoning under <c>thinking</c> (older builds: <c>text</c>) — the durable reasoning trace.</summary>
    private static string ReadThinkingText(JsonElement block) =>
        ReadString(block, "thinking") is { Length: > 0 } t ? t : ReadString(block, "text");

    /// <summary>Tool name → normalized kind, tolerant (Claude's built-ins: Bash, Edit/Write/MultiEdit/NotebookEdit, Read/Grep/Glob, MCP tools).</summary>
    private static AgentEventKind ClassifyTool(string name)
    {
        var n = name.ToLowerInvariant();

        if (n.Contains("bash") || n.Contains("shell") || n.Contains("exec")) return AgentEventKind.CommandExecuted;
        if (n.Contains("edit") || n.Contains("write") || n.Contains("patch") || n.Contains("notebook")) return AgentEventKind.FileChanged;

        return AgentEventKind.ToolCall;
    }

    /// <summary>A one-line rendering of a tool_use block — its most descriptive input field, else the tool name.</summary>
    private static string ToolText(JsonElement block)
    {
        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "command", "file_path", "path", "pattern", "description" })
                if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    return s;

        return ReadString(block, "name");
    }

    /// <summary>The final result line — Claude's <c>result</c> event carries the run's summary text (string) plus an is_error flag.</summary>
    private static string ReadResultText(JsonElement root)
    {
        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s) return s;

        return ReadString(root, "subtype") is { Length: > 0 } sub ? sub : "result";
    }

    /// <summary>
    /// A claude-code <c>result</c> line is a FAILURE when it sets <c>is_error: true</c>, or (when that flag is
    /// absent) carries an error-flavored <c>subtype</c> (<c>error_during_execution</c> / <c>error_max_turns</c>).
    /// Such a result must surface as an <see cref="AgentEventKind.Error"/> event, not <c>Completed</c> — otherwise a
    /// failed run (e.g. a gateway 429) renders on the timeline as a clean "done". An explicit <c>is_error</c> wins;
    /// only when it's missing do we fall back to the subtype.
    /// </summary>
    private static bool IsErrorResult(JsonElement root)
    {
        if (root.TryGetProperty("is_error", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return e.GetBoolean();

        return ReadString(root, "subtype").Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tool_result's content is either a string or an array of {type,text} blocks — flatten to the first text.</summary>
    private static string ReadResultBlockText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";

        return "";
    }

    private static JsonDocument? TryParse(string s)
    {
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return null; }
    }

    private static string ReadString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
