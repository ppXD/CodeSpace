using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Mcp;
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
public sealed class ClaudeCodeHarness : IAgentHarness, IModelCredentialProjector, IMcpHarnessDeclaration, ISingletonDependency
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

    private const string AnthropicProvider = "Anthropic";

    private const string DefaultVersion = "2.1.0";

    private const string DefaultCommand = "claude";

    public string Kind => HarnessKind;

    public string Version => System.Environment.GetEnvironmentVariable(VersionEnvVar) is { Length: > 0 } v ? v : DefaultVersion;

    public IReadOnlyList<string> Models { get; } = new[] { "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5" };

    public SandboxSpec BuildInvocation(AgentTask task)
    {
        // --output-format stream-json REQUIRES --verbose in --print mode (the CLI rejects it otherwise).
        var args = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };

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
            Environment = task.Environment,
            TimeoutSeconds = task.TimeoutSeconds,
            // Isolate Claude Code's config dir per run so it ignores the operator's personal ~/.claude.
            ConfigHomeEnvVars = new[] { ConfigDirEnvVar },
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

        var type = ReadString(root, "type");
        if (type.Length == 0) return Array.Empty<AgentEvent>();

        if (type.Contains("result", StringComparison.OrdinalIgnoreCase))
            return new[] { new AgentEvent { Kind = IsErrorResult(root) ? AgentEventKind.Error : AgentEventKind.Completed, Text = ReadResultText(root), Data = root } };

        if (type is "assistant" or "user")
            return ReadContentEvents(root);

        if (type is "system") return Array.Empty<AgentEvent>();   // init / setup lines carry no run step

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

        if (exitCode == 0)
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage };

        // Surface the most actionable text we have: an explicit Error event, else the CLI's final
        // message (on a non-zero exit that's the failure reason — e.g. a provider 401), else the bare
        // exit code. Folding the summary in here means it reaches AgentRun.error and the node's failure
        // message, instead of the run failing with an opaque "claude exited with code 1".
        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"claude exited with code {exitCode}";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage };
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
        if (!string.IsNullOrEmpty(credential.BaseUrl)) env[BaseUrlEnvVar] = credential.BaseUrl;

        return env;
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
