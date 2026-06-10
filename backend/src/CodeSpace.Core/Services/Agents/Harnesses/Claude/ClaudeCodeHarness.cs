using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
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
public sealed class ClaudeCodeHarness : IAgentHarness, ISingletonDependency
{
    public const string HarnessKind = "claude-code";

    /// <summary>Air-gapped operators pin a private build via this env var (Rule 8). Renaming it breaks their pin — see the pin test.</summary>
    public const string VersionEnvVar = "CODESPACE_CLAUDE_CODE_VERSION";

    private const string DefaultVersion = "2.1.0";

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

        args.Add("--permission-mode");
        args.Add(PermissionMode(task.Permissions));

        args.Add(task.Goal);   // the prompt is the trailing positional argument

        return new SandboxSpec
        {
            Command = "claude",
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

        var type = ReadString(root, "type");
        if (type.Length == 0) return null;

        if (type.Contains("result", StringComparison.OrdinalIgnoreCase))
            return new AgentEvent { Kind = AgentEventKind.Completed, Text = ReadResultText(root), Data = root };

        if (type is "assistant" or "user")
            return ReadContentEvent(root);

        if (type is "system") return null;   // init / setup lines carry no run step

        return new AgentEvent { Kind = AgentEventKind.Warning, Text = type, Data = root };   // unknown → surfaced, never dropped
    }

    public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary)
                       ?? events.LastOrDefault(e => e.Kind == AgentEventKind.Completed)
                       ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        if (exitCode == 0)
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles };

        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text ?? $"claude exited with code {exitCode}";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = summary, ChangedFiles = changedFiles, Error = error };
    }

    /// <summary>ReadOnly → plan (analysis, no edits); Workspace → bypassPermissions (autonomous within the OS sandbox, the Codex workspace-write analogue). The Autonomy dial refines this mapping later.</summary>
    private static string PermissionMode(AgentPermissions permissions) =>
        permissions.WriteScope == AgentWriteScope.ReadOnly ? "plan" : "bypassPermissions";

    /// <summary>Classify an assistant/user turn from the first meaningful content block (text → message, tool_use → command/file/tool, tool_result → command output).</summary>
    private static AgentEvent? ReadContentEvent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;

            var blockType = ReadString(block, "type");

            if (blockType == "text" && ReadString(block, "text") is { Length: > 0 } text)
                return new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = text, Data = block.Clone() };

            if (blockType == "tool_use")
                return new AgentEvent { Kind = ClassifyTool(ReadString(block, "name")), Text = ToolText(block), Data = block.Clone() };

            if (blockType == "tool_result" && ReadResultBlockText(block) is { Length: > 0 } resultText)
                return new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = resultText, Data = block.Clone() };
        }

        return null;
    }

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
