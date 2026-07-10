using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The decision-critic HARD-GATE's escalation surface (triad S8) — the third marker-carrying ask card, the sibling
/// of <see cref="SupervisorPlanConfirmation"/> and <see cref="SupervisorApprovalRequest"/>. When a Gate-mode critic
/// disapproves a decision AND the one bounded re-decide is STILL disapproved, the decision does NOT execute:
/// the decorator returns this <c>ask_human</c> instead — the run parks on the standard card carrying the blocked
/// verb, the reviewer's rationale, and the evidence-attached issues, and the HUMAN becomes the tie-breaker.
/// The policy ladder in one mechanism: model-critic → self-revision → human.
///
/// <para>The answer closes the loop next turn: an <c>approve</c> reply is a ONE-SHOT absolution — the decorator
/// skips review for the immediately-following decision (the operator saw the critique and said proceed); any other
/// reply is guidance the decider naturally reads from the answered ask in its context. One-shot by POSITION (the
/// bypass applies only while the answered escalation is the LATEST prior decision), so absolution never leaks to
/// later turns. Deterministic given (decision, verdict) — a replayed turn re-derives the same card bytes → the
/// same idempotency key.</para>
/// </summary>
public static class SupervisorGateEscalation
{
    /// <summary>The marker phrase EVERY gate-escalation question carries — the stable tail the decorator matches to recognise its OWN card (vs a plan confirmation / an approval / a content ask). Pinned by a unit test so a reword is a visible decision.</summary>
    public const string EscalationMarker = "Reply 'approve' to proceed with this decision despite the review, or describe what to do instead.";

    /// <summary>Cap on the issues quoted into the card — the card is a summary; the full verdict lives on the tape.</summary>
    private const int MaxQuotedIssues = 3;

    /// <summary>Build the escalation ask for a still-disapproved decision — the card carries the blocked verb + the reviewer's rationale + the evidence-attached issues, so the human rules on the CRITIQUE, not a mystery. When the FIRST review is supplied, the card also names which issues PERSISTED unchanged through the revision (P1b-2 convergence), so the operator sees what the revision could not move, not just that it failed twice.</summary>
    public static SupervisorDecision IntoAskHuman(SupervisorDecision blocked, CriticVerdict verdict, CriticVerdict? priorVerdict = null) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        ServerAuthored = true,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = QuestionFor(blocked, verdict, priorVerdict) }, AgentJson.Options),
    };

    /// <summary>
    /// The one-shot absolution read: true — with <paramref name="approved"/> set — ONLY when the LATEST prior
    /// decision is an ANSWERED escalation card. Approve ⇒ the next decision proceeds unreviewed (the human ruled);
    /// any other answer ⇒ guidance (the decider already sees it in its context; the review still runs on the result).
    /// Positional latest-only, so an old absolution can never silently disarm a later turn's gate.
    /// </summary>
    public static bool TryReadAnswer(IReadOnlyList<SupervisorPriorDecision> priorDecisions, out bool approved)
    {
        approved = false;

        var last = priorDecisions.Count > 0 ? priorDecisions[^1] : null;

        if (last == null || !IsEscalationCard(last)) return false;

        var answer = SupervisorOutcome.ReadAskHumanAnswer(last.OutcomeJson);

        if (answer == null) return false;

        approved = answer.TrimStart().StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    /// <summary>A decision is this gate's escalation card iff it is an ask_human whose question carries the escalation marker.</summary>
    public static bool IsEscalationCard(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.AskHuman && QuestionCarriesMarker(decision.PayloadJson);

    /// <summary>An ANSWERED escalation card — the human RULED on a blocked decision (approve or redirect). The no-progress fold counts it as engagement, exactly like an answered plan-confirmation card. The marker itself is text-matchable, but the ANSWER only exists once a resolved Action wait wrote it — so a counted card always cost a real human interaction; a model minting marker text gains nothing unattended.</summary>
    public static bool IsAnsweredEscalationCard(SupervisorPriorDecision decision) =>
        IsEscalationCard(decision) && SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson) != null;

    /// <summary>Whether an ask_human payload's question carries the escalation marker — payload-level, so a content ask or another gate's card never matches.</summary>
    public static bool QuestionCarriesMarker(string? askHumanPayloadJson)
    {
        if (string.IsNullOrEmpty(askHumanPayloadJson)) return false;

        try
        {
            var root = JsonDocument.Parse(askHumanPayloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("question", out var q)
                && q.ValueKind == JsonValueKind.String && (q.GetString() ?? "").Contains(EscalationMarker, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Per-fragment cap on the quoted rationale / issue text — the card is a HEADLINE, not the review dossier (the full evidence-attached verdicts are their own journal beats since H1), so a verbose critic can't turn the ask into a wall of text.</summary>
    internal const int MaxQuotedChars = 200;

    /// <summary>The escalation question — COMPACT by design: the blocked verb, the reviewer's clipped rationale, and up to <see cref="MaxQuotedIssues"/> clipped issue headlines (no evidence dumps — the full verdicts live on the run's REVIEW beats). When the first review is supplied, it names the issues that PERSISTED through the revision (convergence), so the human sees what the revision could not resolve. Bounded so the parked card reads in seconds.</summary>
    private static string QuestionFor(SupervisorDecision blocked, CriticVerdict verdict, CriticVerdict? priorVerdict)
    {
        var builder = new StringBuilder();

        builder.Append($"An independent reviewer blocked the supervisor's '{blocked.Kind}' decision (twice — the revised decision did not satisfy it either). Reviewer: {Clip(verdict.Rationale)}");

        // Convergence: name what the revision could NOT move (present in BOTH reviews) AND what it newly INTRODUCED —
        // a sharper prompt than "still disapproved": the operator sees the unmovable problem AND any fresh regression
        // the revision caused, not just a flat issue list. Falls back to the second verdict's issues with no prior.
        var report = priorVerdict is null ? null : Review.CriticConvergence.Assess(priorVerdict.Issues, verdict.Issues);

        if (report is { Persisting.Count: > 0 } or { Introduced.Count: > 0 })
        {
            if (report.Persisting.Count > 0) AppendIssueList(builder, " The revision did NOT resolve: ", report.Persisting);
            if (report.Introduced.Count > 0) AppendIssueList(builder, " The revision INTRODUCED: ", report.Introduced);
        }
        else if (verdict.Issues.Count > 0)
        {
            AppendIssueList(builder, " Issues: ", verdict.Issues);
        }

        builder.Append(" The full verdicts are on this run's review steps.");
        builder.Append(' ').Append(EscalationMarker);

        return builder.ToString();
    }

    /// <summary>Append a labelled, bounded issue-headline list to the card — up to <see cref="MaxQuotedIssues"/> clipped headlines with a "+N more" count (no evidence dumps — the card is a headline, the full verdicts are their own review beats).</summary>
    private static void AppendIssueList(StringBuilder builder, string label, IReadOnlyList<CriticIssue> issues)
    {
        builder.Append(label);
        builder.Append(string.Join("; ", issues.Take(MaxQuotedIssues).Select(i => Clip(i.Text))));
        if (issues.Count > MaxQuotedIssues) builder.Append($" (+{issues.Count - MaxQuotedIssues} more)");
        builder.Append('.');
    }

    private static string Clip(string text) => text.Length <= MaxQuotedChars ? text : string.Concat(text.AsSpan(0, MaxQuotedChars).TrimEnd(), "…");
}
