using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 pinning tests for rejection-reason wire strings. These land in
/// <c>workflow_run_request.error</c>; analytics dashboards and operator filter queries
/// match on the literal value, so a silent rename breaks every dashboard.
/// </summary>
public class WorkflowRunRequestRejectionReasonsTests
{
    [Theory]
    [InlineData("signature_invalid",      nameof(WorkflowRunRequestRejectionReasons.SignatureInvalid))]
    [InlineData("webhook_inactive",       nameof(WorkflowRunRequestRejectionReasons.WebhookInactive))]
    [InlineData("event_not_mapped",       nameof(WorkflowRunRequestRejectionReasons.EventNotMapped))]
    [InlineData("no_matching_activation", nameof(WorkflowRunRequestRejectionReasons.NoMatchingActivation))]
    public void Rejection_reason_string_form_is_pinned(string expected, string constantName)
    {
        var actual = typeof(WorkflowRunRequestRejectionReasons).GetField(constantName)!.GetValue(null) as string;
        actual.ShouldBe(expected,
            $"WorkflowRunRequestRejectionReasons.{constantName} MUST be wire string '{expected}' — operators filter " +
            "audit views on these literals. Rename → migration to update every existing row, OR don't rename.");
    }
}
