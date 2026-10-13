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

    // ── HasBearerToken: whose token alone resolves the wait ───────────────────
    // Only a Callback is resumed by presenting its token with no session (the anonymous callback route), so only its token
    // is withheld from every projection and stored as a digest. Every other kind's token is a run id or needs runs.decide.
    // Every known kind is listed so a new one is a reviewed decision here.

    [Theory]
    [InlineData(WorkflowWaitKinds.Callback, true)]
    [InlineData(WorkflowWaitKinds.Timer, false)]
    [InlineData(WorkflowWaitKinds.Approval, false)]
    [InlineData(WorkflowWaitKinds.Action, false)]
    [InlineData(WorkflowWaitKinds.Decision, false)]
    [InlineData(WorkflowWaitKinds.Subworkflow, false)]
    [InlineData(WorkflowWaitKinds.AgentRun, false)]
    [InlineData(WorkflowWaitKinds.SupervisorDecision, false)]
    [InlineData(WorkflowWaitKinds.SupervisorAgentWaits, false)]
    [InlineData(WorkflowWaitKinds.SupervisorInfraPark, false)]
    public void HasBearerToken_is_true_only_for_the_anonymously_resumed_kind(string waitKind, bool expected) =>
        WorkflowWaitKinds.HasBearerToken(waitKind).ShouldBe(expected);

    // ── ProjectedToken: what a run read shows in place of a bearer token ─────
    // A read never carries a Callback's token. It carries a fixed placeholder instead of null because every SPA build
    // since the pending-wait decoder shipped (frontend/src/api/workflows.ts, decodeWorkflowRunPendingWaitObservation)
    // refuses a wait whose token is not a 1..128-character string — a null showed "Pending action unavailable" on a
    // previous-version SPA during the deploy. frontend/src/api/workflowRunPendingWaitApi.test.ts decodes this literal.

    [Theory]
    [InlineData(WorkflowWaitKinds.Callback, WorkflowWaitKinds.WithheldBearerToken)]
    [InlineData(WorkflowWaitKinds.Timer, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.Approval, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.Action, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.Decision, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.Subworkflow, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.AgentRun, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.SupervisorDecision, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.SupervisorAgentWaits, "0123456789abcdef0123456789abcdef")]
    [InlineData(WorkflowWaitKinds.SupervisorInfraPark, "0123456789abcdef0123456789abcdef")]
    public void ProjectedToken_withholds_only_a_bearer_token(string waitKind, string expected) =>
        WorkflowWaitKinds.ProjectedToken(waitKind, "0123456789abcdef0123456789abcdef").ShouldBe(expected);

    [Fact]
    public void WithheldBearerToken_is_a_value_every_spa_decodes_and_no_callback_route_can_resume_with()
    {
        // Pinned: the frontend test decodes this exact literal through the decoder every SPA build ships.
        WorkflowWaitKinds.WithheldBearerToken.ShouldBe("withheld-reload-the-page-to-reveal");

        WorkflowWaitKinds.WithheldBearerToken.Length.ShouldBeInRange(1, 128, "the SPA decoder's bounds for a wait token");
        WorkflowWaitKinds.WithheldBearerToken.Length.ShouldNotBe(32, "this version's callback route admits only {token:length(32)}");
        WorkflowWaitKinds.WithheldBearerToken.ShouldNotMatch("^[0-9a-f]+$", "never a shape any version mints, so no wait row's token can equal it — not even on an older build whose route admits any segment");
    }
}
