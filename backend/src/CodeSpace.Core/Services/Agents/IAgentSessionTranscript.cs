namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// OPTIONAL sibling capability of <see cref="IAgentHarness"/> (Rule 7 — a capability a harness opts into, not a
/// widening of the core interface): a harness whose CLI persists a RESUMABLE session transcript on disk — the file
/// its <c>--resume</c> reads — implements this to LOCATE that file (relative to the per-run config home) after a run,
/// so the executor can capture it durably (the per-run config home is reaped) for a later CONTINUE to restore. Two
/// on-disk shapes both fit: a COMPUTABLE path keyed on the cwd + id (Claude's
/// <c>projects/&lt;sanitized-cwd&gt;/&lt;sessionId&gt;.jsonl</c>) and a SEARCH-only one whose name carries the id but a
/// timestamp not known ahead of time (Codex's <c>sessions/&lt;date&gt;/rollout-&lt;ts&gt;-&lt;id&gt;.jsonl</c>, found by
/// globbing under the config home). A harness with no addressable resumable transcript simply doesn't implement this —
/// capture then no-ops and a continue cold-starts. The RESTORE side (writing a carried transcript back so the CLI's
/// <c>--resume</c> finds it) is the harness's own <c>BuildInvocation</c> concern (its <c>ConfigHomeFiles</c>), because
/// the restore path can differ from the capture path (Claude re-keys on the NEW run's cwd; Codex writes a deterministic
/// id-named rollout the CLI globs) — this capability is CAPTURE-locate only.
/// </summary>
public interface IAgentSessionTranscript
{
    /// <summary>
    /// The config-home-relative path of the EXISTING session transcript to capture for (<paramref name="workspaceDirectory"/>,
    /// <paramref name="sessionId"/>), located within <paramref name="configHome"/> (the run's on-disk config home). A
    /// harness whose path is computable ignores <paramref name="configHome"/> (Claude → <c>projects/&lt;cwd&gt;/&lt;id&gt;.jsonl</c>);
    /// one whose file is search-only globs under it (Codex → the id-bearing <c>rollout-…</c> the CLI wrote). Null when
    /// unaddressable (no session id, no cwd for a cwd-keyed layout, or no matching file on disk). For a cwd-keyed layout
    /// the cwd MUST be the RESOLVED path the process ran in — the encoding keys on it (the sharpest P3 hazard). The
    /// executor security-clamps the returned path within the config home, so a search must stay under it.
    /// </summary>
    string? SessionTranscriptRelativePath(string configHome, string? workspaceDirectory, string? sessionId);
}
