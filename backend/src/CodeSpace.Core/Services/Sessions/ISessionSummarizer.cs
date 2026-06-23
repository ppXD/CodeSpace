namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Maintains a session's rolling <c>WorkSession.Summary</c> — the LLM-distilled context of OLDER turns that have
/// scrolled out of the digest's recent verbatim window. On a continue, folds the turns newly above the summary
/// watermark into the summary (incremental, never a full re-summarize) so a long thread keeps its early context
/// without unbounded prompt growth. The distillation companion to <see cref="ISessionContextBuilder"/> (which renders
/// the recent turns verbatim) — together they bound the injected context while preserving the whole thread's memory.
/// </summary>
public interface ISessionSummarizer
{
    /// <summary>
    /// Fold any turns that have scrolled out of the recent window (above the current <c>SummaryThroughTurnIndex</c>)
    /// into the session's rolling summary, advancing the watermark. A NO-OP when the thread still fits the recent
    /// window (short thread ⇒ no summary ⇒ byte-identical digest). BEST-EFFORT + FAIL-OPEN: no model in the team's
    /// pool, or an LLM error, leaves the summary unchanged (the digest still carries the recent window) and never
    /// throws into the launch. The write is staged on the shared request-scoped DbContext, committing atomically with
    /// the run that triggered it.
    /// </summary>
    Task EnsureSummaryUpToDateAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken);
}
