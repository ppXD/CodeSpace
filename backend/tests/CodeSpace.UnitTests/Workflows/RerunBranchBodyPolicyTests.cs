using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The pure D7-5 allowlist deciding WHICH node types may be a re-run <c>flow.map</c> branch BODY node. It is
/// fail-closed: a body is REFUSED unless it is provably safe to re-run. This pins the truth table in isolation;
/// the engine wiring (re-stage / approval-gate / refuse-and-write-nothing) is proven end-to-end in
/// RerunMapBranchFlowTests, and the drift-detector over the REAL node registry lives there too (it needs the
/// full DI-built manifests).
/// </summary>
[Trait("Category", "Unit")]
public class RerunBranchBodyPolicyTests
{
    private static NodeManifest Manifest(bool canSuspend, bool sideEffecting, bool rerunnableWhenSuspendable, NodeKind kind = NodeKind.Regular) => new()
    {
        DisplayName = "n",
        Category = "Test",
        Kind = kind,
        CanSuspend = canSuspend,
        IsSideEffecting = sideEffecting,
        IsRerunnableWhenSuspendable = rerunnableWhenSuspendable,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    [Theory]
    // canSuspend, sideEffecting, rerunnableWhenSuspendable, kind  → expectedRefused
    [InlineData(false, false, false, NodeKind.Regular, false)]  // pure compute/read → ADMIT
    [InlineData(false, true, false, NodeKind.Regular, false)]   // purely side-effecting (git write / http POST) → ADMIT (routes through the D7-3 gate)
    [InlineData(true, false, true, NodeKind.Regular, false)]    // agent.code / subworkflow: CanSuspend + opted-in + NOT side-effecting → ADMIT (re-stage, no gate)
    [InlineData(true, false, false, NodeKind.Regular, true)]    // un-opted suspendable (wait_* / supervisor / sleep — agent.code & subworkflow opt in) → REFUSE
    [InlineData(true, true, true, NodeKind.Regular, true)]      // BOTH side-effecting AND suspendable (chat.post_message) → REFUSE even if opted in (belt-and-suspenders)
    [InlineData(true, true, false, NodeKind.Regular, true)]     // both-flagged, not opted in → REFUSE (doubly)
    [InlineData(false, false, false, NodeKind.Map, true)]       // nested container Map → REFUSE
    [InlineData(false, false, false, NodeKind.Loop, true)]      // nested container Loop → REFUSE
    [InlineData(false, false, false, NodeKind.Try, true)]       // nested container Try → REFUSE
    public void IsRefusedAsBranchBody_enforces_the_failclosed_allowlist(bool canSuspend, bool sideEffecting, bool rerunnableWhenSuspendable, NodeKind kind, bool expectedRefused)
    {
        RerunBranchBodyPolicy.IsRefusedAsBranchBody(Manifest(canSuspend, sideEffecting, rerunnableWhenSuspendable, kind))
            .ShouldBe(expectedRefused);
    }

    [Fact]
    public void A_new_suspendable_node_is_refused_by_default_failclosed()
    {
        // The opt-in flag defaults false, so a freshly-authored CanSuspend node (no IsRerunnableWhenSuspendable set,
        // not side-effecting, Regular kind) is REFUSED until its substrate is proven. This is the whole point of the
        // allowlist over a wholesale relaxation.
        var freshSuspendable = Manifest(canSuspend: true, sideEffecting: false, rerunnableWhenSuspendable: false);

        RerunBranchBodyPolicy.IsRefusedAsBranchBody(freshSuspendable).ShouldBeTrue();
    }

    [Fact]
    public void The_both_flag_arm_refuses_independently_of_the_opt_in()
    {
        // A node that is BOTH IsSideEffecting AND CanSuspend stays refused even if it (wrongly) set the opt-in:
        // the D7-3 gate fires the effect on the approved walk then mis-skips on the node's own resume. The explicit
        // both-flag arm guarantees the refusal class-wide, not per-node.
        RerunBranchBodyPolicy.IsRefusedAsBranchBody(Manifest(canSuspend: true, sideEffecting: true, rerunnableWhenSuspendable: true)).ShouldBeTrue();
        RerunBranchBodyPolicy.IsRefusedAsBranchBody(Manifest(canSuspend: true, sideEffecting: true, rerunnableWhenSuspendable: false)).ShouldBeTrue();
    }
}
