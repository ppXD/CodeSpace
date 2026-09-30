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
/// trial count there, never a partial publish. A conflicted set therefore says that nothing landed, with the applied
/// count as context only, and names each contribution that did not apply beside the branch that still keeps its work.</para>
///
/// <para><b>Withheld is not an outcome.</b> A unit whose own definition-of-done rejected it never reaches the integrator
/// (<see cref="RunIntegrationContributions.Withheld"/>), so it appears in no <see cref="IntegrationResult"/>; its own
/// clause is appended to every account, because it is true of a Clean candidate and a skipped run alike.</para>
///
/// <para><b>Bounded.</b> The text rides a prompt whose own budget is enforced elsewhere (the map's
/// <c>promptBudgetChars</c>), so it carries its own: <see cref="MaxChars"/>, cut with an ellipsis — negligible against
/// that budget, and never a silent drop of the head of the account, which is always first.</para>
/// </summary>
public static class RunIntegrationSummary
{
    /// <summary>The account's length bound, in UTF-16 code units (the cut never splits a surrogate pair).</summary>
    public const int MaxChars = 2_000;

    /// <summary>The account of an integration the integrator actually ran, plus what was withheld before it.</summary>
    public static string ForResult(IntegrationResult result, IReadOnlyList<WithheldContribution> withheld) => Bounded(Headline(result) + WithheldClause(withheld));

    /// <summary>The account of an integration that never ran — nothing integrable, or no base revision to integrate from — plus what was withheld before it (the usual reason nothing was integrable).</summary>
    public static string ForSkipped(string reason, IReadOnlyList<WithheldContribution> withheld) => Bounded($"Integration skipped: {reason}." + WithheldClause(withheld));

    private static string Headline(IntegrationResult result) => result.Status switch
    {
        IntegrationStatus.Clean => $"Integration: {result.AppliedCount} contribution(s) landed on {result.IntegratedBranch}.",
        IntegrationStatus.Conflicted => ConflictedHeadline(result),
        IntegrationStatus.Empty => $"Integration landed nothing: {result.Reason ?? "there was nothing to integrate"}.",
        _ => $"Integration {result.Status}: {result.AppliedCount} of {result.Outcomes.Count} contribution(s) applied.",
    };

    private static string ConflictedHeadline(IntegrationResult result)
    {
        var headline = $"Integration conflicted: no integrated branch was published, so none of this run's work landed on one ({result.AppliedCount} of {result.Outcomes.Count} contribution(s) applied cleanly, but integration is all-or-nothing).";
        var notApplied = GitIntegrateNode.NotApplied(result).Select(Describe).ToList();

        if (notApplied.Count > 0) return $"{headline} Conflicted and withheld from the integrated branch: {string.Join(", ", notApplied)}.";

        return result.Reason is { Length: > 0 } reason ? $"{headline} Reason: {reason}." : headline;
    }

    /// <summary>One contribution that did not apply, beside where its work is kept. A bystander — never attempted, blocked only because ANOTHER contribution's problem stopped the set — is marked so it never reads as an equal failure.</summary>
    private static string Describe(ContributionOutcome outcome)
    {
        var kept = outcome.FallbackBranch is { Length: > 0 } branch ? branch : "no branch to review";

        return outcome.Skipped ? $"{outcome.Label} → {kept} (not attempted)" : $"{outcome.Label} → {kept}";
    }

    private static string WithheldClause(IReadOnlyList<WithheldContribution> withheld) =>
        withheld.Count == 0 ? "" : $" Withheld before integration: {string.Join(", ", withheld.Select(w => $"{w.Label} — {w.Reason}"))}.";

    /// <summary>Cut to <see cref="MaxChars"/> with an ellipsis, stepping back over a high surrogate the cut would otherwise strand — an unpaired half is text <c>System.Text.Json</c> refuses to encode, which would fail the node over prose.</summary>
    private static string Bounded(string text)
    {
        if (text.Length <= MaxChars) return text;

        var cut = char.IsHighSurrogate(text[MaxChars - 2]) ? MaxChars - 2 : MaxChars - 1;

        return text[..cut] + "…";
    }
}
