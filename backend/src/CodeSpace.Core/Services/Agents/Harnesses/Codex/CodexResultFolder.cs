using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Harnesses.Codex;

/// <summary>
/// Codex's OWN result folder: the reduction <see cref="CodexHarness"/> builds its <see cref="AgentRunResult"/> from,
/// and the only place its summary-fallback chain and its "codex exited with code …" wording live. It composes the
/// shared <see cref="AgentResultFold"/> because Codex happens to want that reduction — not because the seam imposes
/// it; a reduction only this harness needed would be a field HERE.
/// </summary>
internal sealed class CodexResultFolder : IAgentEventFolder
{
    /// <summary>Codex's own sentence for a model refusing a streamed request as over its window (a <c>response.failed</c>, reworded) — observed from Codex 0.147.0.</summary>
    private const string StreamedOverflowSentence = "ran out of room in the model's context window";

    /// <summary>The JSON-RPC error data Codex writes to stderr when it refuses an input past its own character cap, before any request (observed from 0.142.2 and 0.147.0).</summary>
    private const string InputCapRefusal = "\"input_error_code\":\"input_too_large\"";

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

        // The fallback chains over the LAST EVENT of each kind, so a FinalSummary whose text is blank still wins
        // (LastTextOf returns "" for a kind that was seen blank, null only for one never seen).
        var summary = _fold.LastTextOf(AgentEventKind.FinalSummary) ?? _fold.LastTextOf(AgentEventKind.AssistantMessage);

        // D3b-i: cost-accounting figure — Codex emits a cumulative token_count event per turn, so the last
        // recognizable usage is the run total. Null when the stream carried none. Useful on failure too.
        var usage = facts.TokenUsage;

        // P3.1a: capture the CLI thread id (Codex's thread.started event carries thread_id) — the handle a rerun
        // threads back as `codex exec resume <id>` to CONTINUE this conversation. Null when the stream carried none.
        var sessionId = facts.SessionId;
        var model = facts.Model;

        // exitCode==0 only means the CLI process itself didn't crash — Codex can still emit turn.failed mid-run
        // (surfaced during parsing as an Error event) while the wrapping process exits clean. Trusting the exit code
        // alone would silently report that failed turn as Succeeded.
        if (exitCode == 0 && !_fold.ReportedFailure)
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage, SessionId = sessionId, Model = model };

        // Prefer an explicit Error event, else the CLI's final message (on a non-zero exit that's the
        // failure reason — e.g. a provider 401), else the bare exit code — so the real reason reaches
        // AgentRun.error and the node failure instead of an opaque "codex exited with code 1".
        // The last rung is where a stderr-only death lands — the CLI printed a plain-text fatal on the OTHER
        // opening and this side dropped it as non-JSON — so that rung, and only that rung, folds it back in.
        var error = _fold.LastTextOf(AgentEventKind.Error)
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? AgentDiagnosticExcerpt.Explain($"codex exited with code {SandboxExitCode.Describe(exitCode)}", diagnostics);

        var exitReason = _fold.ReportedFailure && RefusedAsOverContextWindow(_lastErrorLine) ? AgentTerminalOutcomeReader.ContextWindowExceededExitReason
                         : diagnostics.Contains(InputCapRefusal, StringComparison.Ordinal) ? FailureCodes.SandboxArgumentTooLong
                         : exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId, Model = model };
    }

    /// <summary>
    /// Whether Codex's own terminal <c>turn.failed</c> says the model refused the request as larger than its window:
    /// the provider's typed error code it relays, or its own sentence for the streamed refusal. Only that event — the
    /// turn's verdict, which the agent cannot author — is read, so an agent message about context windows never is.
    /// </summary>
    private static bool RefusedAsOverContextWindow(JsonElement? line)
    {
        if (line is not { ValueKind: JsonValueKind.Object } failed || !failed.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "turn.failed") return false;

        if (!failed.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return false;

        var text = message.GetString() ?? "";

        return ProviderErrorCode(text) == "context_length_exceeded" || text.Contains(StreamedOverflowSentence, StringComparison.Ordinal);
    }

    /// <summary>The <c>error.code</c> of a provider error body Codex relays as its message verbatim, or null when the message is not one.</summary>
    private static string? ProviderErrorCode(string message)
    {
        try
        {
            using var body = JsonDocument.Parse(message);

            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
