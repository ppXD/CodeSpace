using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// PURE delta trim of a full <see cref="JournalView"/> to only what a client hasn't seen — the <c>?since=</c> streaming
/// support. Keeps the whole structure (turns, statuses, the head cursor, and the focused turn's <see cref="JournalTurn.StepCount"/>)
/// so the client can reconcile live state, but drops the focused turn's steps AT OR BEFORE the client's last-seen cursor,
/// so a live poll re-sends only the NEW steps. An unrecognized <c>since</c> (old / forged / truncated) trims NOTHING —
/// the client re-syncs on the full set rather than silently losing steps.
///
/// <para>APPEND-OPTIMIZED, not exhaustive: this delivers steps whose cursor sorts AFTER the client's — the append case.
/// A step that lands BELOW the client's cursor (an out-of-order backfill: cross-source clock skew, a late-flushed row)
/// cannot ride an append-only delta. The client detects this WITHOUT losing data: after applying a delta, if its
/// accumulated step count is less than <see cref="JournalTurn.StepCount"/> (which the delta preserves), a below-cursor
/// step exists → it re-fetches the FULL journal (omit <c>?since</c>). So the delta is the cheap common-case path; the
/// full fetch is the correctness backstop. Unit-pinned; the query handler composes it after the projector.</para>
/// </summary>
public static class JournalDelta
{
    public static JournalView After(JournalView view, string? since)
    {
        if (JournalCursor.Decode(since) is null) return view;   // no / unrecognized cursor → full view (never drop)

        var turns = view.Turns
            .Select(t => t.Focused ? t with { Steps = t.Steps.Where(s => JournalCursor.Compare(s.Cursor, since!) > 0).ToList() } : t)
            .ToList();

        return view with { Turns = turns };
    }
}
