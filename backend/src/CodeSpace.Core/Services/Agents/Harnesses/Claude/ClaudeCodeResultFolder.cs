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
    private readonly AgentResultFold _fold = new();

    public void Add(AgentEvent normalized) => _fold.Add(normalized);

    public AgentRunResult BuildResult(AgentRunFacts facts, int exitCode)
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
        var error = _fold.LastTextOf(AgentEventKind.Error)
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"claude exited with code {SandboxExitCode.Describe(exitCode)}";

        var exitReason = exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId, Model = model };
    }
}
