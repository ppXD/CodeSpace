using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Harnesses.Codex;

/// <summary>
/// Adapter for OpenAI's Codex CLI. Drives <c>codex exec --json …</c> (the non-interactive mode built
/// for scripts/CI) and normalizes its JSONL event stream into <see cref="AgentEvent"/>s.
///
/// <para><b>Fidelity note:</b> the CLI surface (<c>exec</c>, <c>--json</c>, <c>--model</c>, <c>--sandbox</c>)
/// is well-documented, so <see cref="BuildInvocation"/> is exact. The JSONL event <i>type</i> names are
/// version-dependent, so <see cref="ParseEvent"/> classifies by tolerant keyword match and maps anything
/// it can't place to <see cref="AgentEventKind.Warning"/> (surfaced, never dropped). The exact type→kind
/// table is calibrated against real <c>codex exec --json</c> output when execution is wired (B0.4); the
/// normalization shape tested here is the stable contract.</para>
/// </summary>
public sealed class CodexHarness : IAgentHarness, IModelCredentialProjector, ISingletonDependency
{
    public const string HarnessKind = "codex-cli";

    /// <summary>Air-gapped operators pin a private build via this env var (Rule 8). Renaming it breaks their pin — see the pin test.</summary>
    public const string VersionEnvVar = "CODESPACE_CODEX_CLI_VERSION";

    /// <summary>Air-gapped operators (and tests) repoint the Codex binary via this env var — an absolute path or a PATH name. Renaming it breaks their pin — see the pin test.</summary>
    public const string CommandEnvVar = "CODESPACE_CODEX_CLI_PATH";

    /// <summary>The env var Codex reads its model API key from (OpenAI + OpenAI-compatible providers). Pinned by a test (Rule 8) — the agent authenticates with whatever lands here.</summary>
    public const string ApiKeyEnvVar = "OPENAI_API_KEY";

    /// <summary>The env var Codex reads a base-URL override from (OpenRouter / a self-hosted OpenAI-compatible gateway / a local Ollama). Pinned by a test (Rule 8).</summary>
    public const string BaseUrlEnvVar = "OPENAI_BASE_URL";

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
        args.Add(task.Goal);

        return new SandboxSpec
        {
            Command = ResolveCommand(),
            Args = args,
            WorkingDirectory = task.WorkspaceDirectory,
            Environment = task.Environment,
            TimeoutSeconds = task.TimeoutSeconds,
        };
    }

    public AgentEvent? ParseEvent(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) return null;

        JsonElement root;
        using (var doc = TryParse(line))
        {
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            root = doc.RootElement.Clone();
        }

        var type = ReadType(root);
        if (type is null) return null;

        return new AgentEvent { Kind = MapKind(type), Text = ReadText(root, type), Data = root };
    }

    public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary) ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        if (exitCode == 0)
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles };

        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text ?? $"codex exited with code {exitCode}";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = summary, ChangedFiles = changedFiles, Error = error };
    }

    /// <summary>Codex drives OpenAI + any OpenAI-API-compatible endpoint (OpenRouter, a self-hosted gateway, a local Ollama) via a base-URL override.</summary>
    public IReadOnlyList<string> SupportedProviders { get; } = new[] { "OpenAI", "OpenRouter", "Ollama", "Custom" };

    /// <summary>
    /// Project a resolved credential onto Codex's env: the api key under <see cref="ApiKeyEnvVar"/> (omitted for a
    /// keyless local provider) and any base-URL override under <see cref="BaseUrlEnvVar"/>. Codex's OpenAI provider
    /// reads both, so the same shape covers OpenAI (key only), OpenRouter / Custom (key + base URL), and Ollama
    /// (base URL only).
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
