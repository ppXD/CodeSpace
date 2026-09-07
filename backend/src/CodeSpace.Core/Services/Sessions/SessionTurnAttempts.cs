using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Resolves a turn's EFFECTIVE attempt out of its full lineage (the turn run + every rerun/replay fork sharing
/// <c>RootRunId ?? Id</c>) — the shared choke point every IN-PROCESS session read (<see cref="SessionProjection"/>,
/// <see cref="SessionBranchResolver"/>, <see cref="SessionContextBuilder"/>, <see cref="SessionSummarizer"/>) calls
/// directly, so none of them can read a superseded attempt's stale <c>OutputsJson</c>/branch while a later rerun in
/// the same lineage actually succeeded. Pure (no DB) — the caller loads the lineage's rows.
///
/// <para><c>SessionTurnsContextSource</c> cannot call this — its resolution has to run INSIDE PostgreSQL (a rerun's
/// row never crosses the DB/CLR boundary at all unless it wins) — so it mirrors the SAME rule in SQL instead:
/// "newest <c>Success</c>, else newest overall" is identical, but its final tie-break (<c>l.id DESC</c>, PostgreSQL's
/// own <c>uuid</c> byte ordering) is a SEPARATE implementation from this method's <c>(CreatedDate, Id)</c> ordering
/// (.NET's <see cref="Guid"/> ordering) — the two are pinned to agree by an oracle test rather than by sharing code.</para>
///
/// <para>A lineage-terminal <see cref="WorkflowRunStatus.Success"/> attempt wins (a rerun exists precisely to fix a
/// failed attempt — its success is the turn's real outcome), else the newest attempt by <c>(CreatedDate, Id)</c> wins
/// (never silently invisible even when every attempt failed).</para>
/// </summary>
internal static class SessionTurnAttempts
{
    /// <summary>One lineage member's identity + the fields the "which attempt wins" decision needs.</summary>
    internal readonly record struct AttemptRow(Guid Id, WorkflowRunStatus Status, DateTimeOffset CreatedDate);

    /// <summary>The effective attempt's id — the newest <see cref="WorkflowRunStatus.Success"/> row if any exists, else the newest row overall. <paramref name="rows"/> must be non-empty (the caller's own lineage group).</summary>
    internal static Guid ResolveEffectiveId(IEnumerable<AttemptRow> rows)
    {
        var ordered = rows.OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).ToList();

        var succeeded = ordered.Where(r => r.Status == WorkflowRunStatus.Success).ToList();

        return (succeeded.Count > 0 ? succeeded : ordered)[^1].Id;
    }
}
