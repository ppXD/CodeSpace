using System.Text;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// Detects whether a bounded revise loop is CONVERGING (each pass resolves something) or OSCILLATING (re-raising the
/// same unaddressed problem) — the "can't tell improving from stuck" gap. Two weak models (producer + critic) can
/// re-flag a semantically identical issue round after round; without this the budget is always fully consumed at full
/// token cost before the loop gives up. A stable text FINGERPRINT recognises "the same issue" across rounds despite a
/// weak model's trivial re-wording, so an oscillating loop can stop EARLY and escalate with "this persisted", instead
/// of burning every remaining round on an unmovable problem.
///
/// <para>Pure + generic: the executor's in-run revise loop compares consecutive revise SIGNALS (<see cref="SameSignal"/>
/// over the composed reason — oracle detail or critic feedback); the supervisor's hard-Gate ladder compares two rounds'
/// structured issue SETS (<see cref="Assess"/>) to name what persisted vs what the revision resolved. One fingerprint,
/// one convergence rule, shared by every revise/re-review path.</para>
/// </summary>
public static class CriticConvergence
{
    /// <summary>
    /// A stable fingerprint of a problem's text — lower-cased, with every run of NOISE punctuation collapsed to a
    /// single space and trimmed, but the RELATIONAL operators <c>&lt; &gt; =</c> PRESERVED. Robust to the trivial
    /// differences a weak model introduces when it re-words the SAME finding (spacing, capitalisation, a stray period),
    /// so "the same issue" is recognised without a brittle exact-string match — while keeping OPPOSITE conditions
    /// distinct (<c>x &gt;= 0</c> and <c>x &lt;= 0</c> must not collapse to the same fingerprint and mask a real change).
    /// Blank in → empty (an empty fingerprint never matches another — a missing signal is never a stall).
    /// </summary>
    public static string Fingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            var kept = char.IsLetterOrDigit(ch) || ch is '<' or '>' or '=';   // relational operators carry meaning — opposite conditions must stay distinct

            if (kept)
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>The fingerprint of one issue — its text alone (evidence + severity are not identity; the same finding re-worded with different evidence is still the same finding).</summary>
    public static string Fingerprint(CriticIssue issue) => Fingerprint(issue.Text);

    /// <summary>
    /// Whether two consecutive revise SIGNALS are the same unaddressed problem — the executor's early-stop test. True
    /// only when both fingerprint to the SAME non-empty value: the prior round produced the identical reason, so it
    /// changed nothing material and another pass will re-produce it. A blank/absent signal is never a stall (fail
    /// toward continuing — the safe direction, since the budget bound still backstops).
    /// </summary>
    public static bool SameSignal(string? priorReason, string? currentReason)
    {
        var current = Fingerprint(currentReason);
        return current.Length > 0 && current == Fingerprint(priorReason);
    }

    /// <summary>
    /// The delta between two rounds' issue sets, by fingerprint: what the revision RESOLVED (in prior, gone now), what
    /// PERSISTS (in both — the unmoved problems), and what it newly INTRODUCED (in current, not before). One issue per
    /// distinct fingerprint (a model that repeats itself is not listed twice), keeping the current round's instance for
    /// a persisting fingerprint (its latest wording/severity). Used to name a non-converging escalation precisely ("the
    /// revision did not resolve: …; and introduced: …").
    /// </summary>
    public static ConvergenceReport Assess(IReadOnlyList<CriticIssue> prior, IReadOnlyList<CriticIssue> current)
    {
        var priorPrints = prior.Select(Fingerprint).Where(p => p.Length > 0).ToHashSet();
        var currentPrints = current.Select(Fingerprint).Where(p => p.Length > 0).ToHashSet();

        var resolved = DistinctByFingerprint(prior).Where(i => !currentPrints.Contains(Fingerprint(i))).ToList();
        var persisting = DistinctByFingerprint(current).Where(i => priorPrints.Contains(Fingerprint(i))).ToList();
        var introduced = DistinctByFingerprint(current).Where(i => !priorPrints.Contains(Fingerprint(i))).ToList();

        return new ConvergenceReport { Resolved = resolved, Persisting = persisting, Introduced = introduced };
    }

    /// <summary>One issue per distinct non-empty fingerprint, first-wins — so a self-repeating model doesn't list the same finding twice on a human card.</summary>
    private static IEnumerable<CriticIssue> DistinctByFingerprint(IReadOnlyList<CriticIssue> issues)
    {
        var seen = new HashSet<string>();

        foreach (var issue in issues)
            if (Fingerprint(issue) is { Length: > 0 } print && seen.Add(print))
                yield return issue;
    }
}

/// <summary>The convergence delta between two revise rounds — what a revision resolved, what it left unmoved, and what it newly introduced. All by fingerprint identity (text), one issue per distinct fingerprint, carrying the current round's instance for the persisting set.</summary>
public sealed record ConvergenceReport
{
    public IReadOnlyList<CriticIssue> Resolved { get; init; } = Array.Empty<CriticIssue>();
    public IReadOnlyList<CriticIssue> Persisting { get; init; } = Array.Empty<CriticIssue>();
    public IReadOnlyList<CriticIssue> Introduced { get; init; } = Array.Empty<CriticIssue>();
}
