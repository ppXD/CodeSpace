using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Mcp;
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
public sealed class CodexHarness : IAgentHarness, IModelCredentialProjector, IMcpHarnessDeclaration, ISingletonDependency
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

    private const string DefaultVersion = "0.2.0";

    private const string DefaultCommand = "codex";

    public string Kind => HarnessKind;

    public string Version => System.Environment.GetEnvironmentVariable(VersionEnvVar) is { Length: > 0 } v ? v : DefaultVersion;

    public IReadOnlyList<string> Models { get; } = new[] { "gpt-5.3-codex", "gpt-5.4", "gpt-5.4-codex" };

    public SandboxSpec BuildInvocation(AgentTask task)
    {
        var args = new List<string> { "exec", "--json" };

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

        args.Add("--sandbox");
        args.Add(SandboxMode(task.Permissions));

        // Point Codex at a custom gateway (when one was projected) BEFORE the prompt positional — Codex parses `-c`
        // overrides as flags, so they must precede the goal.
        AppendModelProviderConfig(args, task);

        // B1 asymmetry: Codex exec has NO native system-prompt flag (Claude uses --append-system-prompt). Prepending the
        // operating contract to the prompt positional conflates it with the goal (and pollutes goal-reading consumers),
        // so the Codex projection is deferred to a proper non-goal channel (AGENTS.md / a config key) — see the B1 follow-up.
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
            // The agent reaches the network only when its permissions allow it (the sandbox severs egress otherwise).
            AllowNetwork = task.Permissions.Network == AgentNetworkAccess.On,
        };
    }

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

        // Codex's `exec --json` emits ONE event object per JSONL line, so a line maps to a single event (the full
        // root is retained as Data, and a large payload is offloaded downstream). The list contract lets a future
        // multi-block stream surface several — Codex just never packs more than one per line today.
        return new[] { new AgentEvent { Kind = MapKind(type), Text = ReadText(root, type), Data = root } };
    }

    public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary) ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        // D3b-i: cost-accounting figure — Codex emits a cumulative token_count event per turn, so the last
        // recognizable usage is the run total. Null when the stream carried none. Useful on failure too.
        var usage = AgentTokenUsageReader.TryRead(events);

        if (exitCode == 0)
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage };

        // Prefer an explicit Error event, else the CLI's final message (on a non-zero exit that's the
        // failure reason — e.g. a provider 401), else the bare exit code — so the real reason reaches
        // AgentRun.error and the node failure instead of an opaque "codex exited with code 1".
        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"codex exited with code {Sandbox.SandboxExitCode.Describe(exitCode)}";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage };
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
        Override($"model_providers.{ModelProviderId}.base_url={baseUrl}");
        Override($"model_providers.{ModelProviderId}.wire_api={ModelProviderWireApi}");
        Override($"model_providers.{ModelProviderId}.env_key={ApiKeyEnvVar}");
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
