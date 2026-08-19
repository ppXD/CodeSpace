using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// PURE delta trim of a full <see cref="JournalView"/> to only the steps a client hasn't seen — the <c>?since=</c>
/// streaming support. Keeps the whole structure (turns, statuses, the head cursor, and every turn's
/// <see cref="JournalTurn.StepCount"/>) so the client can reconcile live state, and drops steps the client provably
/// already holds. An unrecognized <c>since</c> (old / forged / truncated) trims NOTHING — the client re-syncs on the
/// full set rather than silently losing steps.
///
/// <para>WHAT IS TRIMMED, per turn:
/// the FOCUSED turn keeps only the steps whose cursor sorts AFTER the client's (its steps are still growing);
/// a NON-FOCUSED TERMINAL turn keeps NONE (its walk is finished, so it can never gain a step, and the client got it on
/// the full fetch that issued the cursor);
/// a NON-FOCUSED LIVE turn keeps ALL of its steps — the client's single cursor belongs to the focused turn's run and says
/// nothing about any other run's progress, so there is nothing to trim against. Sessions with concurrent turns therefore
/// still carry those turns in full, and only the finished history is elided.</para>
///
/// <para>APPEND-OPTIMIZED, not exhaustive: the focused turn's trim delivers steps whose cursor sorts AFTER the client's —
/// the append case. A step that lands BELOW the client's cursor (an out-of-order backfill: cross-source clock skew, a
/// late-flushed row) cannot ride an append-only delta. The client detects this WITHOUT losing data via the count check
/// below.</para>
///
/// <para>THE COUNT CHECK is the correctness backstop for every trim above, and the reason each is safe to make. Every
/// turn's <see cref="JournalTurn.StepCount"/> survives the trim as the server's FULL total. After applying a delta the
/// client compares, per turn, its accumulated step count against that total; ANY mismatch means its accumulation is not
/// what the server holds — a below-cursor backfill, a step that MOVED (an in-place upgrade re-stamps its cursor: the
/// in-flight reviewer beat's OccurredAt advances when its verdict lands), a step that VANISHED (a terminally-failed
/// reviewer's beat yields nothing), or a non-focused turn that finished between two polls and gained steps the client
/// never saw. The client re-fetches the FULL journal (omit <c>since</c>) and REPLACES its accumulation. Replacing rather
/// than merging is what makes this terminate: a full fetch is authoritative, so a mismatch cannot recur from the same
/// cause. A delta consumer must therefore upsert BY STEP ID and run the per-turn count check on every applied delta;
/// <c>mergeJournalDelta</c> in <c>frontend/src/lib/journalDelta.ts</c> is that consumer.</para>
/// </summary>
public static class JournalDelta
{
    public static JournalView After(JournalView view, string? since)
    {
        if (JournalCursor.Decode(since) is null) return view;   // no / unrecognized cursor → full view (never drop)

        return view with { Turns = view.Turns.Select(turn => Trim(turn, since!)).ToList() };
    }

    /// <summary>Drop the steps this turn cannot have changed since the client's cursor was issued. StepCount is untouched, so any mistake here surfaces as a client-side count mismatch and a full re-fetch — never as a lost step.</summary>
    private static JournalTurn Trim(JournalTurn turn, string since)
    {
        if (turn.Focused) return turn with { Steps = turn.Steps.Where(step => JournalCursor.Compare(step.Cursor, since) > 0).ToList() };

        return WorkflowRunState.IsTerminal(turn.Status) ? turn with { Steps = Array.Empty<JournalStep>() } : turn;
    }
}
