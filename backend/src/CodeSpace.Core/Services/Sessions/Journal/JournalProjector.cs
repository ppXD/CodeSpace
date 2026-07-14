using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Default <see cref="IJournalProjector"/> — mirrors the room projector's shape (reuse the turn skeleton from
/// <see cref="ISessionReadService"/>, one query) but produces the JOURNAL: EVERY turn is walked into its chronological
/// <see cref="JournalStep"/>s (via <see cref="IJournalWalk"/> — one heavy per-run read per turn), so each turn's full
/// journal is available on expand, not just the focused one. Entering by a run id focuses the SPECIFIC attempt it names
/// — a prior/reran attempt shows ITS OWN status, timing, and walked steps (not the newest), matching the room.
/// READ-ONLY. <see cref="JournalView.Cursor"/> = the FOCUSED turn's newest step cursor (the delta head); past turns are
/// terminal so their steps are immutable (a caching candidate to avoid re-walking on every live poll).
/// </summary>
public sealed class JournalProjector : IJournalProjector, IScopedDependency
{
    private readonly ISessionReadService _sessions;
    private readonly IJournalWalk _walk;
    private readonly ISessionTurnCache _cache;

    public JournalProjector(ISessionReadService sessions, IJournalWalk walk, ISessionTurnCache cache)
    {
        _sessions = sessions;
        _walk = walk;
        _cache = cache;
    }

    public async Task<JournalView?> ProjectByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetByRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        // The requested run IS the anchor — opening a prior attempt's run focuses THAT attempt's flow, not the latest.
        return detail == null ? null : await BuildAsync(detail, detail.AnchorTurnIndex, runId, teamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalView?> ProjectAsync(Guid sessionId, Guid? focusRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetDetailAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        if (detail == null) return null;

        var focus = focusRunId is { } fr ? TurnIndexOf(detail, fr) : null;

        return await BuildAsync(detail, focus, focusRunId, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The turn a run belongs to — its identity, its newest attempt, or any nested attempt. Null when the run isn't a turn here.</summary>
    private static int? TurnIndexOf(SessionDetail detail, Guid runId) =>
        detail.Turns.FirstOrDefault(t => t.TurnRunId == runId || t.RunId == runId || (t.Attempts?.Any(a => a.RunId == runId) ?? false))?.TurnIndex;

    private async Task<JournalView> BuildAsync(SessionDetail detail, int? focusTurnIndex, Guid? anchorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var focusedTurn = (focusTurnIndex is { } fi ? detail.Turns.FirstOrDefault(t => t.TurnIndex == fi) : null) ?? detail.Turns.LastOrDefault();

        var turns = new List<JournalTurn>(detail.Turns.Count);
        var cursor = "";

        foreach (var turn in detail.Turns)
        {
            // Walk EVERY turn into its steps so each turn's full journal is available on expand, not just the focused one.
            // The focused turn honours the requested attempt; every other turn walks its own latest run (anchor null).
            var isFocused = focusedTurn != null && turn.TurnIndex == focusedTurn.TurnIndex;
            var focus = ResolveFocus(turn, isFocused ? anchorRunId : null);
            // A non-focused TERMINAL turn's steps are immutable ledger projection, so serve them from the cache — the past
            // turns aren't re-walked on every 2s poll. The focused turn (live / chosen attempt) is always walked fresh.
            var steps = !isFocused && WorkflowRunState.IsTerminal(turn.RunStatus)
                ? await _cache.GetOrAddJournalAsync(focus.RunId, async () => await _walk.WalkAsync(focus.RunId, teamId, cancellationToken).ConfigureAwait(false) ?? Array.Empty<JournalStep>()).ConfigureAwait(false)
                : await _walk.WalkAsync(focus.RunId, teamId, cancellationToken).ConfigureAwait(false) ?? Array.Empty<JournalStep>();

            if (isFocused && steps.Count > 0) cursor = steps[^1].Cursor;   // the delta head stays the focused turn's newest step

            turns.Add(BuildTurn(turn, focus, steps, isFocused));
        }

        return new JournalView
        {
            SessionId = detail.Id,
            Title = detail.Title,
            Kind = detail.Kind,
            Status = detail.Status,
            Cursor = cursor,
            // The scroll anchor exists only on a run-entry (the run's turn); a bare session-entry has none (open at the latest).
            AnchorTurnIndex = anchorRunId is not null ? focusedTurn?.TurnIndex : null,
            Turns = turns,
        };
    }

    private sealed record FocusRun(Guid RunId, WorkflowRunStatus Status, DateTimeOffset CreatedDate, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, bool IsLatest);

    /// <summary>
    /// Resolve which attempt to focus + walk. The latest (or a run that isn't one of this turn's attempts, or a bare
    /// session-entry) reuses the turn skeleton — the newest attempt. A specific PRIOR attempt is resolved from the
    /// skeleton's attempt ladder (its own run id + status + start), so its OWN flow is walked + its own status shown,
    /// NOT the newest attempt's — the parity the room's attempt-switcher relies on. Pure (the skeleton already carries
    /// the attempts, so no extra read); the prior attempt's precise wall-clock isn't on the skeleton, so its duration reads null.
    /// </summary>
    private static FocusRun ResolveFocus(SessionTurn turn, Guid? anchorRunId)
    {
        var latest = new FocusRun(turn.RunId, turn.RunStatus, turn.CreatedDate, turn.StartedAt, turn.CompletedAt, IsLatest: true);

        if (anchorRunId is not { } anchor || anchor == turn.RunId) return latest;

        var attempt = turn.Attempts?.FirstOrDefault(a => a.RunId == anchor);

        return attempt is null ? latest : new FocusRun(attempt.RunId, attempt.Status, attempt.CreatedDate, StartedAt: null, CompletedAt: null, IsLatest: false);
    }

    /// <summary>A fully-walked turn — its chronological steps + attempt ladder. <paramref name="focused"/> marks the one turn the view is anchored on (scroll target + the ?since delta head); every turn is walked, so all are rich.</summary>
    private static JournalTurn BuildTurn(SessionTurn turn, FocusRun focus, IReadOnlyList<JournalStep> steps, bool focused) => new()
    {
        TurnIndex = turn.TurnIndex,
        TurnRunId = turn.TurnRunId,
        RunId = focus.RunId,
        Status = focus.Status,
        UserMessage = turn.UserMessage is { Length: > 0 } m ? m : null,
        // The turn-level result is the LATEST attempt's; a focused PRIOR attempt shows none (its steps carry the story).
        Summary = focus.IsLatest && turn.Result is { Length: > 0 } r ? r : null,
        At = focus.CreatedDate,
        DurationMs = DurationOf(focus.CreatedDate, focus.StartedAt, focus.CompletedAt),
        Focused = focused,
        Attempts = MapAttempts(turn, focusedRunId: focus.RunId),
        Steps = steps,
        StepCount = steps.Count,   // the FULL total — a ?since delta trims Steps but keeps this, so the client can self-heal
    };

    /// <summary>Project the skeleton's attempt ladder (already loaded — no extra read) into the wire shape, marking the focused attempt on the focused turn. Empty when the turn was never reran (the skeleton carries no ladder).</summary>
    private static IReadOnlyList<JournalAttempt> MapAttempts(SessionTurn turn, Guid? focusedRunId) =>
        turn.Attempts is not { Count: > 0 } attempts
            ? Array.Empty<JournalAttempt>()
            : attempts.Select(a => new JournalAttempt
            {
                AttemptNumber = a.AttemptNumber,
                RunId = a.RunId,
                Status = a.Status,
                At = a.CreatedDate,
                SourceType = a.SourceType,
                RerunFromNodeId = a.RerunFromNodeId,
                IsLatest = a.IsLatest,
                Focused = a.RunId == focusedRunId,
                Error = a.Error,
            }).ToList();

    /// <summary>The turn/attempt wall-clock — anchored on the immutable enqueue time (a resumed run resets StartedAt, which would under-report the whole elapsed); live elapsed since it started while in-flight, null before then. Mirrors the room projector.</summary>
    private static long? DurationOf(DateTimeOffset createdDate, DateTimeOffset? startedAt, DateTimeOffset? completedAt)
    {
        if (completedAt is { } end)
        {
            var span = (long)(end - createdDate).TotalMilliseconds;
            return span >= 0 ? span : null;
        }

        if (startedAt is not { } start) return null;

        var ms = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds;
        return ms >= 0 ? ms : null;
    }
}
