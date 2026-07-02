using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — every <see cref="WorkflowWaitKinds"/> value is a wire-format string: it's persisted in
/// <c>workflow_run_wait.wait_kind</c> AND pinned by the DB CHECK constraint (see the 00xx migrations).
/// Renaming one orphans every parked run of that kind and trips the CHECK, so pin the literals and
/// make a rename a compile-error-visible decision. <see cref="WorkflowEngine.ValidateWaitKind"/> is
/// the engine-side gate that must admit exactly the same set.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowWaitKindsTests
{
    [Theory]
    [InlineData(WorkflowWaitKinds.Timer, "Timer")]
    [InlineData(WorkflowWaitKinds.Approval, "Approval")]
    [InlineData(WorkflowWaitKinds.Callback, "Callback")]
    [InlineData(WorkflowWaitKinds.Subworkflow, "Subworkflow")]
    [InlineData(WorkflowWaitKinds.Action, "Action")]
    [InlineData(WorkflowWaitKinds.AgentRun, "AgentRun")]
    [InlineData(WorkflowWaitKinds.SupervisorDecision, "SupervisorDecision")]
    [InlineData(WorkflowWaitKinds.Decision, "Decision")]
    public void Wait_kind_literals_are_pinned(string actual, string expected)
    {
        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(WorkflowWaitKinds.Timer)]
    [InlineData(WorkflowWaitKinds.Approval)]
    [InlineData(WorkflowWaitKinds.Callback)]
    [InlineData(WorkflowWaitKinds.Subworkflow)]
    [InlineData(WorkflowWaitKinds.Action)]
    [InlineData(WorkflowWaitKinds.AgentRun)]
    [InlineData(WorkflowWaitKinds.SupervisorDecision)]
    [InlineData(WorkflowWaitKinds.Decision)]
    public void ValidateWaitKind_admits_every_known_kind(string kind)
    {
        WorkflowEngine.ValidateWaitKind("node-1", kind).ShouldBe(kind);
    }

    [Fact]
    public void ValidateWaitKind_rejects_an_unknown_kind()
    {
        var ex = Should.Throw<Exception>(() => WorkflowEngine.ValidateWaitKind("node-1", "Telepathy"));

        ex.Message.ShouldContain("node-1");
        ex.Message.ShouldContain("Telepathy");
        ex.Message.ShouldContain("SupervisorDecision", customMessage: "the error must list the admitted kinds so an author sees SupervisorDecision is now valid");
    }

    // ── IsOperatorReissuable: the reissue-verb allow-list ─────────────────────
    // Only the SIGNAL-driven waits that can strand with no backstop (a dropped Timer wake, a dead Callback) are
    // operator-reissuable; every decision- / completion-driven kind is refused (they resolve via their own verb or a
    // reconciler backstop, and a blind reissue would feed the node a bogus payload). Hard-pinned so WIDENING the set is
    // a conscious decision — a new wait kind is fail-closed (not reissuable) until deliberately added.

    [Theory]
    [InlineData(WorkflowWaitKinds.Timer, true)]
    [InlineData(WorkflowWaitKinds.Callback, true)]
    [InlineData(WorkflowWaitKinds.Approval, false)]
    [InlineData(WorkflowWaitKinds.Action, false)]
    [InlineData(WorkflowWaitKinds.Decision, false)]
    [InlineData(WorkflowWaitKinds.Subworkflow, false)]
    [InlineData(WorkflowWaitKinds.AgentRun, false)]
    [InlineData(WorkflowWaitKinds.SupervisorDecision, false)]
    [InlineData(WorkflowWaitKinds.SupervisorAgentWaits, false)]
    public void IsOperatorReissuable_allows_only_the_signal_driven_stranding_kinds(string waitKind, bool expected) =>
        WorkflowWaitKinds.IsOperatorReissuable(waitKind).ShouldBe(expected);

    [Fact]
    public void IsOperatorReissuable_is_fail_closed_for_an_unknown_kind() =>
        WorkflowWaitKinds.IsOperatorReissuable("SomeFutureKind").ShouldBeFalse("a new wait kind is NOT operator-reissuable until deliberately added to the allow-list");
}
