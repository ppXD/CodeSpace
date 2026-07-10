namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// H2 (strict action validation) — the PURE sanitizer that keeps a MODEL-authored <c>ask_human</c> question from
/// posing as a SERVER-authored card. The server's own gates and marker cards are recognized on the tape purely by
/// pinned question text: the delivery/I3 gate prefixes drive H1's adjudication release, and the three marker
/// sentences drive structural authority reads (<see cref="SupervisorPlanConfirmation.LastApprovedDelivery"/>
/// pairs a plan with "the very next confirmation card"; a minted marker card answered 'approve' could forge
/// plan-confirmation / delivery authorization — the FATAL-2 laundering family). Stripping the reserved tokens at
/// the DECIDER boundary (before <c>ApplyPostDecisionGate</c>, so genuinely server-authored substitutions never
/// pass through it) closes the whole channel structurally: whatever the model writes, the PERSISTED question can
/// no longer carry a server identity.
///
/// <para>Sanitize-not-reject, deliberately: the model's question still reaches the human intact minus the stolen
/// identity (zero extra turns, no silent work-drop — the ask still functions), and the removal is logged by the
/// caller. Pure + deterministic → a replay re-derives the identical clamp and idempotency key.</para>
/// </summary>
public static class SupervisorAskQuestionClamp
{
    /// <summary>Every server-reserved question token, sourced from the owning consts directly — a marker rename can never drift this list.</summary>
    public static IReadOnlyList<string> ReservedTokens { get; } = new[]
    {
        SupervisorDeliveryGate.QuestionPrefix,
        SupervisorPublishGate.QuestionPrefix,
        SupervisorPlanConfirmation.ConfirmationMarker,
        SupervisorApprovalRequest.ApprovalMarker,
        SupervisorGateEscalation.EscalationMarker,
    };

    /// <summary>What a question that consisted ONLY of reserved tokens collapses to — legible to the human and the decider, never a blank (a blank question is the rejected-ask path).</summary>
    public const string AllReservedFallback = "(the model's question consisted only of reserved server-gate text and was removed — ask it to restate the question in plain words)";

    /// <summary>
    /// The question with every reserved token removed (ordinal, repeated until stable — a removal that splices two
    /// fragments into a NEW token occurrence is re-stripped, bounded defensively). Returns the ORIGINAL string
    /// instance when nothing matched, so the caller's byte-identical fast path is a reference check away.
    /// </summary>
    public static string Sanitize(string question)
    {
        var current = question;

        for (var pass = 0; pass < 10; pass++)
        {
            var before = current;

            foreach (var token in ReservedTokens)
                current = current.Replace(token, string.Empty, StringComparison.Ordinal);

            if (ReferenceEquals(before, current) || before == current)
                return current == question ? question : Finish(current);
        }

        return Finish(current);
    }

    private static string Finish(string stripped)
    {
        var trimmed = stripped.Trim();

        return trimmed.Length == 0 ? AllReservedFallback : trimmed;
    }
}
