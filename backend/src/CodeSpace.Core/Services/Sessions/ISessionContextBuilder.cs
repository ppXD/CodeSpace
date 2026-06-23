namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Builds the THREAD CONTEXT a continuing run is primed with — a compact, deterministic digest of the session's
/// PRIOR top-level turns (what each was asked + what it produced), so a follow-up agent builds on earlier work
/// instead of starting cold. The launch path folds the result into the new run's grounding (the agent's prompt);
/// this is the intelligence heart of the session layer, not the FK.
/// </summary>
public interface ISessionContextBuilder
{
    /// <summary>
    /// Render the session's prior top-level turns (goal + status + result summary + produced branch) as a single
    /// grounding block, newest-window-first-then-chronological and bounded for size. Returns <c>null</c> when the
    /// session has no prior turn yet (nothing to carry forward). Team-scoped (defence in depth).
    /// </summary>
    Task<string?> BuildAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken);
}
