using System.Text.RegularExpressions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// What <c>git.integrate_run</c> says about its own outcome: ONE account of what actually landed, rendered on every
/// pass (clean, conflicted, empty, skipped, resumed) and read by the plan-map synth beside the per-subtask results.
/// Pure, so the exact words pin without a database — the words are the contract: the reduce is told to state what
/// landed and to name anything conflicted or withheld as NOT delivered, and it can only do that if the sentence it
/// reads says so.
///
/// <para><b>Landed is not applied.</b> A non-Clean integration published NO branch — <see cref="IntegrationResult.IntegratedBranch"/>
/// is null on every such status and the integrator resets its clone to base on an abort — so <c>AppliedCount</c> is a
/// trial count there, never a partial publish. A conflicted set says that nothing landed and where the work stays; a
/// set that applied cleanly but was not PUSHED (no credential, or the remote branch advanced) says exactly that
/// instead, because nothing conflicted.</para>
///
/// <para><b>Which, not how many.</b> A Clean account names the contributions that landed, and flags the ones whose
/// own agent run did not succeed (<see cref="RunIntegrationContributions.LandedFailedSubtasks"/>): a subtask that died
/// after editing files is not withheld by its own grade — it is ungraded — so its partial diff rides the candidate while
/// its results entry reads as an error. Two qualifiers, because the two shapes say different things about the diff: a
/// subtask whose acceptance check had PASSED was verified and failed afterwards, any other left a partial nothing
/// checked. Every label is an engine coordinate the reader cannot place, so each is written
/// <c>subtask N (label)</c> with N the 1-based position in the results the reduce reads beside this text.</para>
///
/// <para><b>Withheld is not an outcome.</b> A unit whose own definition-of-done rejected it, or whose output review did
/// not approve it, never reaches the integrator (<see cref="RunIntegrationContributions.Withheld"/>), so it appears in no
/// <see cref="IntegrationResult"/>; its own clause is appended to every account, because it is true of a Clean candidate
/// and a skipped run alike.</para>
///
/// <para><b>Review is stated, never quoted.</b> A resumed pass says only WHETHER a reviewer approved the retry — a fixed
/// sentence. The reviewer's free-text comment is a person's words on their way into a model's prompt, so it stays on
/// the node's outputs.</para>
///
/// <para><b>Bounded, and lists give way first.</b> The text rides a prompt whose own budget is enforced elsewhere (the
/// map's <c>promptBudgetChars</c>), so it carries its own: <see cref="MaxChars"/>. Prose is never shortened; a LIST is,
/// from its end, saying how many entries it left out and where the full list lives — least important first (what
/// landed or conflicted, then the failed subtasks that landed — the verified ones before the unverified partials — and
/// last the withheld units, which every account carries). Free text quoted from an integrator is clipped, so the bound
/// holds by construction and never by cutting the tail.</para>
/// </summary>
public static class RunIntegrationSummary
{
    /// <summary>The account's length bound, in UTF-16 code units.</summary>
    public const int MaxChars = 2_000;

    /// <summary>The most an integrator's own free-text reason may contribute to the account.</summary>
    private const int ReasonChars = 300;

    /// <summary>The position a plan-map label carries: <c>{node}#map#{i}</c>, where <c>map</c> is the fan-out node every plan-map graph names it and <c>i</c> the 0-based index the results array is ordered by.</summary>
    private static readonly Regex MapPosition = new(@"#map#(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The account of an integration the integrator actually ran. <paramref name="landedFailed"/> names the landed contributions whose own run did not succeed; <paramref name="reviewApproved"/> is null on a first pass and the reviewer's verdict on the resumed one. What landed comes first, then what qualifies it, then the review, then what never landed.</summary>
    public static string ForResult(IntegrationResult result, IReadOnlyList<WithheldContribution> withheld, IReadOnlyList<LandedFailedSubtask> landedFailed, bool? reviewApproved) =>
        Compose(Headline(result).Concat(LandedFailedClauses(result, landedFailed)).Concat(ReviewClause(reviewApproved)).Concat(WithheldClause(withheld)));

    /// <summary>The account of an integration that never ran — nothing integrable, or no base revision to integrate from. <paramref name="workRemains"/> is true when the run DID produce work (the no-base case), which then stays where its agents left it.</summary>
    public static string ForSkipped(string reason, bool workRemains, IReadOnlyList<WithheldContribution> withheld) =>
        Compose(SkippedClauses(reason, workRemains).Concat(WithheldClause(withheld)));

    private static IEnumerable<Clause> Headline(IntegrationResult result) => result.Status switch
    {
        IntegrationStatus.Clean => LandedClause(result),
        IntegrationStatus.Conflicted => GitIntegrateNode.NotApplied(result).Any() ? ConflictedClauses(result) : new[] { Clause.Fixed(NotPublished(result)) },
        IntegrationStatus.Empty => new[] { Clause.Fixed($"Integration landed nothing: {Quoted(result.Reason) ?? "there was nothing to integrate"}.") },
        _ => new[] { Clause.Fixed($"Integration {result.Status}: {result.AppliedCount} of {result.Outcomes.Count} contribution(s) applied; no integrated branch was reported, so nothing landed on one — {WorkStays}.") },
    };

    private static IEnumerable<Clause> LandedClause(IntegrationResult result)
    {
        var lead = $"Integration: {result.AppliedCount} contribution(s) landed on {result.IntegratedBranch}";
        var landed = result.Outcomes.Select(outcome => Subtask(outcome.Label)).ToList();

        yield return landed.Count > 0 ? Clause.List(lead + ": ", landed, source: null, importance: 0) : Clause.Fixed(lead + ".");
    }

    private static IEnumerable<Clause> ConflictedClauses(IntegrationResult result)
    {
        yield return Clause.Fixed($"Integration conflicted: no integrated branch was published, so none of this run's work landed on one ({result.AppliedCount} of {result.Outcomes.Count} contribution(s) applied cleanly, but integration is all-or-nothing); {WorkStays}.");
        yield return Clause.List(" Conflicted or held back: ", GitIntegrateNode.NotApplied(result).Select(Describe).ToList(), source: "conflicts", importance: 0);
    }

    /// <summary>Every contribution applied, yet nothing was published — the push was refused or there was no credential to push with. Nothing conflicted, so the account must not say so.</summary>
    private static string NotPublished(IntegrationResult result) =>
        $"Integration applied every contribution cleanly but published no branch{(Quoted(result.Reason) is { } reason ? $" ({reason})" : "")}, so none of this run's work landed on one; {WorkStays}.";

    private static IEnumerable<Clause> SkippedClauses(string reason, bool workRemains)
    {
        yield return Clause.Fixed($"Integration skipped: {Quoted(reason)}.");

        if (workRemains) yield return Clause.Fixed($" Nothing landed on an integrated branch; the produced work stays on {AgentsOwn}.");
    }

    private static IEnumerable<Clause> ReviewClause(bool? reviewApproved) =>
        reviewApproved is { } approved ? new[] { Clause.Fixed($" A reviewer {(approved ? "approved" : "rejected")} the retry.") } : Array.Empty<Clause>();

    /// <summary>Only a Clean account has landed work to qualify — a non-Clean set landed nothing, whatever its members' runs did. Two clauses, for two shapes of diff: one nothing checked, and one whose acceptance check passed before its run failed.</summary>
    private static IEnumerable<Clause> LandedFailedClauses(IntegrationResult result, IReadOnlyList<LandedFailedSubtask> landedFailed)
    {
        if (result.Status != IntegrationStatus.Clean) yield break;

        var unverified = landedFailed.Where(subtask => !subtask.AcceptancePassed).Select(subtask => Subtask(subtask.Label)).ToList();
        var verified = landedFailed.Where(subtask => subtask.AcceptancePassed).Select(subtask => Subtask(subtask.Label)).ToList();

        if (unverified.Count > 0) yield return Clause.List(" Landed although its subtask failed (partial, unverified): ", unverified, source: null, importance: 2);

        if (verified.Count > 0) yield return Clause.List(" Landed although its subtask failed afterwards (acceptance passed): ", verified, source: null, importance: 1);
    }

    private static IEnumerable<Clause> WithheldClause(IReadOnlyList<WithheldContribution> withheld) =>
        withheld.Count > 0
            ? new[] { Clause.List(" Withheld before integration: ", withheld.Select(w => $"{Subtask(w.Label)} — {w.Reason}").ToList(), source: "withheld", importance: 3) }
            : Array.Empty<Clause>();

    /// <summary>One contribution that did not apply, beside where its work is kept. A bystander — never attempted, blocked only because ANOTHER contribution's problem stopped the set — is marked so it never reads as an equal failure.</summary>
    private static string Describe(ContributionOutcome outcome)
    {
        var kept = outcome.FallbackBranch is { Length: > 0 } branch ? branch : "no branch to review";

        return outcome.Skipped ? $"{Subtask(outcome.Label)} → {kept} (not attempted)" : $"{Subtask(outcome.Label)} → {kept}";
    }

    /// <summary><c>subtask N (label)</c> for a plan-map label — N the 1-based position in the per-subtask results — else the bare label, which is all a label outside that shape (a supervisor turn's cell) can honestly claim.</summary>
    private static string Subtask(string label) =>
        MapPosition.Match(label) is { Success: true } position && int.TryParse(position.Groups[1].Value, out var index) ? $"subtask {index + 1} ({label})" : label;

    private const string AgentsOwn = "the agents' own branches and patches";

    private const string WorkStays = "the work stays on " + AgentsOwn;

    /// <summary>An integrator's free-text reason, clipped so one verbose message cannot crowd out the lists that matter. Null stays null.</summary>
    private static string? Quoted(string? reason) => reason is { Length: > 0 } ? Clip(reason, ReasonChars) : null;

    /// <summary>Cut to <paramref name="max"/> with an ellipsis, stepping back over a high surrogate the cut would otherwise strand — an unpaired half is text <c>System.Text.Json</c> refuses to encode, which would fail the node over prose.</summary>
    private static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1;

        return text[..cut] + "…";
    }

    /// <summary>
    /// Lay the clauses end to end within <see cref="MaxChars"/>. Nothing is cut while the whole fits. When it does not,
    /// the LISTS shrink — least important first — each to whatever room the rest leaves it, so a list is only ever
    /// cut because a more important one needs the room, and the withheld clause is the last to give any up.
    /// </summary>
    private static string Compose(IEnumerable<Clause> clauses)
    {
        var parts = clauses.ToList();
        var text = parts.Select(part => part.Render(int.MaxValue)).ToArray();

        foreach (var index in Enumerable.Range(0, parts.Count).OrderBy(i => parts[i].Importance))
        {
            var room = MaxChars - text.Where((_, at) => at != index).Sum(part => part.Length);

            if (text[index].Length > room) text[index] = parts[index].Render(room);
        }

        return string.Concat(text);
    }

    /// <summary>
    /// One piece of the account. A FIXED clause is prose and is never shortened. A LIST clause is a lead and its entries,
    /// comma-joined and ended with a full stop; shortened, it keeps the entries that fit and ends with how many it left
    /// out — and, when the full list lives in an output array, which one (<c>… and 58 more — see conflicts[]</c>).
    /// <see cref="Importance"/> orders the cuts: the lowest gives way first.
    /// </summary>
    private sealed record Clause(string Lead, IReadOnlyList<string> Entries, string? Source, int Importance)
    {
        public static Clause Fixed(string text) => new(text, Array.Empty<string>(), null, int.MaxValue);

        public static Clause List(string lead, IReadOnlyList<string> entries, string? source, int importance) => new(lead, entries, source, importance);

        public string Render(int room)
        {
            if (Entries.Count == 0) return Lead;

            var whole = Lead + string.Join(", ", Entries) + ".";

            return whole.Length <= room ? whole : Shortened(KeptWithin(room));
        }

        /// <summary>How many leading entries fit in <paramref name="room"/> beside the closing "… and N more" — reserved at its widest, so it always fits.</summary>
        private int KeptWithin(int room)
        {
            var reserved = Lead.Length + 1 + Omitted(Entries.Count).Length + 2;
            var kept = 0;

            while (kept < Entries.Count && reserved + Entries[kept].Length + 2 <= room)
                reserved += Entries[kept++].Length + 2;

            return kept;
        }

        private string Shortened(int kept) => Lead + string.Join(", ", Entries.Take(kept).Append(Omitted(Entries.Count - kept))) + ".";

        private string Omitted(int count) => $"… and {count} more" + (Source is null ? "" : $" — see {Source}[]");
    }
}
