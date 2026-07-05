using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the SHARED stop-outcome success predicate (<see cref="SupervisorStopPayload.IsSuccessOutcome"/>) — the ONE
/// list the decision-eval scorecard AND the room's degraded RESULT render both key on, so they can't drift on which stop
/// outcomes are a genuine success vs a graceful failure. Pinned so an edit to the word set is a visible decision, and so a
/// future fail-closed outcome (a new give-up stop) can't silently read as a green success.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorStopOutcomeTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("complete")]
    [InlineData("success")]
    [InlineData("succeeded")]
    [InlineData("done")]
    [InlineData("ok")]
    [InlineData("DONE")]         // case-insensitive
    [InlineData(" completed ")]  // trimmed
    public void Genuine_success_labels_are_success(string outcome) =>
        SupervisorStopPayload.IsSuccessOutcome(outcome).ShouldBeTrue();

    [Theory]
    [InlineData("no-decision")]       // NonConformantStop — the reported case
    [InlineData("no-model")]          // NoModelStop / no-pool
    [InlineData("unknown-decision")]  // projector fallback for an unrecognized kind
    [InlineData("aborted")]
    [InlineData("failed")]
    [InlineData("")]
    [InlineData(null)]
    public void Graceful_failure_and_unknown_labels_are_NOT_success(string? outcome) =>
        SupervisorStopPayload.IsSuccessOutcome(outcome).ShouldBeFalse("a fail-closed / unknown outcome must never read as success — the room renders it degraded, the eval aborts it");

    [Fact]
    public void The_nonconformant_outcome_constant_is_not_a_success() =>
        // The exact label NonConformantStop stamps must fall in the graceful-failure set, else the reported non-conformant stop renders green.
        SupervisorStopPayload.IsSuccessOutcome(SupervisorStopPayload.NonConformantOutcome).ShouldBeFalse();
}
