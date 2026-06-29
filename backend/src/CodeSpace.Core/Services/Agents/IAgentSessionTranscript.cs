namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// OPTIONAL sibling capability of <see cref="IAgentHarness"/> (Rule 7 — a capability a harness opts into, not a
/// widening of the core interface): a harness whose CLI persists a RESUMABLE session transcript on disk — the file
/// its <c>--resume</c> reads — implements this to declare WHERE that file lives, relative to the per-run config home,
/// for a given (resolved) workspace cwd + captured session id. The executor reads it after the run and persists it
/// durably (the per-run config home is reaped), so a later CONTINUE can restore the conversation. A harness with no
/// addressable resumable transcript (Codex's date-nested <c>sessions/&lt;date&gt;/rollout-…</c> layout is deferred; a
/// future stateless one) simply doesn't implement this — capture then no-ops and a continue cold-starts.
/// </summary>
public interface IAgentSessionTranscript
{
    /// <summary>
    /// The config-home-relative path of the session transcript for (<paramref name="workspaceDirectory"/>,
    /// <paramref name="sessionId"/>) — e.g. Claude's <c>projects/&lt;sanitized-cwd&gt;/&lt;sessionId&gt;.jsonl</c>.
    /// Null when this run cannot address one (no workspace cwd, or no session id). The cwd MUST be the RESOLVED path
    /// the process actually ran in — the layout keys on its encoding (the sharpest P3 hazard).
    /// </summary>
    string? SessionTranscriptRelativePath(string? workspaceDirectory, string? sessionId);
}
