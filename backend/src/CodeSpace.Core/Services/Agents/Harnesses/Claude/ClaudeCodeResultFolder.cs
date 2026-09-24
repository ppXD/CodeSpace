using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// Claude Code's OWN result folder: the reduction <see cref="ClaudeCodeHarness"/> builds its
/// <see cref="AgentRunResult"/> from, and the only place its summary-fallback chain and its "claude exited with
/// code …" wording live. It composes the shared <see cref="AgentResultFold"/> because Claude happens to want that
/// reduction — not because the seam imposes it; a reduction only this harness needed would be a field HERE.
/// </summary>
internal sealed class ClaudeCodeResultFolder : IAgentEventFolder
{
    /// <summary>
    /// What an OpenAI-compatible gateway (vLLM, LiteLLM) answers an over-long request with, which the CLI passes
    /// through verbatim as <c>terminal_reason: api_error</c> — the one overflow shape it does not stamp as its own.
    /// Observed from Claude Code 2.1.226 answered with each body; read only off a 400 refusal on the result line.
    /// </summary>
    private static readonly string[] GatewayOverflowMarkers = { "maximum context length is", "context_length_exceeded" };

    private readonly AgentResultFold _fold = new();
    private JsonElement? _lastErrorLine;

    public void Add(AgentEvent normalized)
    {
        _fold.Add(normalized);

        if (normalized.Kind == AgentEventKind.Error) _lastErrorLine = normalized.Data;
    }

    public AgentRunResult BuildResult(AgentRunFacts facts, int exitCode, string diagnostics)
    {
        var changedFiles = _fold.ChangedFiles;

        // The fallbacks chain over the LAST EVENT of each kind, so a FinalSummary whose text is blank still wins
        // (LastTextOf returns "" for a kind that was seen blank, null only for one never seen) — the harness reports
        // what the CLI actually said last, never a nicer-looking earlier line.
        var summary = _fold.LastTextOf(AgentEventKind.FinalSummary)
                      ?? _fold.LastTextOf(AgentEventKind.Completed)
                      ?? _fold.LastTextOf(AgentEventKind.AssistantMessage);

        // D3b-i: cost-accounting figure — Claude's final result line carries a usage object; the fold
        // tolerantly extracts input/output tokens from it. Null when the stream carried none. On failure too.
        var usage = facts.TokenUsage;

        // P3.1a: capture the CLI session id (Claude's result line carries session_id) — the handle a rerun
        // threads back as `claude --resume <id>` to CONTINUE this conversation. Null when the stream carried none.
        var sessionId = facts.SessionId;
        var model = facts.Model;

        // exitCode==0 only means the CLI process itself didn't crash — Claude Code's own result line can still
        // carry is_error:true (e.g. a gateway 429 mid-turn), which IsErrorResult already normalizes into an
        // Error event during parsing. Trusting the exit code alone would silently report that failed turn as Succeeded.
        if (exitCode == 0 && !_fold.ReportedFailure)
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage, SessionId = sessionId, Model = model };

        // Surface the most actionable text we have: an explicit Error event, else the CLI's final
        // message (on a non-zero exit that's the failure reason — e.g. a provider 401), else the bare
        // exit code. Folding the summary in here means it reaches AgentRun.error and the node's failure
        // message, instead of the run failing with an opaque "claude exited with code 1".
        // The last rung is where a stderr-only death lands — the CLI printed a plain-text fatal on the OTHER
        // opening and this side dropped it as non-JSON — so that rung, and only that rung, folds it back in.
        var error = _fold.LastTextOf(AgentEventKind.Error)
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? AgentDiagnosticExcerpt.Explain($"claude exited with code {SandboxExitCode.Describe(exitCode)}", diagnostics);

        var exitReason = _fold.ReportedFailure && RefusedAsOverContextWindow(_lastErrorLine) ? AgentTerminalOutcomeReader.ContextWindowExceededExitReason
                         : exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId, Model = model };
    }

    /// <summary>
    /// Whether the CLI's own terminal result line says the model refused the request as larger than its context
    /// window. Read off fields the CLI writes (<c>terminal_reason</c>, <c>api_error_status</c>) and, for the gateway
    /// shape, off the refusal body it relays — never off the agent's prose, which is how a crash's last message or a
    /// rubric's wording would otherwise pass for a diagnosis. A 5xx is the gateway failing, not the model refusing,
    /// so it stays an ordinary, retryable failure whatever its body says.
    /// </summary>
    private static bool RefusedAsOverContextWindow(JsonElement? line)
    {
        if (line is not { ValueKind: JsonValueKind.Object } result) return false;

        var terminalReason = ReadString(result, "terminal_reason");

        if (terminalReason == "prompt_too_long") return true;

        if (terminalReason != "api_error" || !result.TryGetProperty("api_error_status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code != 400) return false;

        var body = ReadString(result, "result");

        return GatewayOverflowMarkers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
