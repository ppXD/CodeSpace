using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The pure truth table for the D7-3 rerun side-effect gate: WHICH nodes get human-gated (a side-effecting
/// node only on a from-node rerun), and how the operator's resolved-approval payload is read (fail-closed —
/// anything but an explicit <c>approved:true</c> means skip). The engine wiring (suspend / resume / skip) is
/// proven end-to-end in RerunFromNodeFlowTests; this pins the decision logic in isolation.
/// </summary>
[Trait("Category", "Unit")]
public class RerunSideEffectGateTests
{
    private static NodeManifest Manifest(bool sideEffecting, NodeKind kind = NodeKind.Regular) => new()
    {
        DisplayName = "n",
        Category = "Test",
        Kind = kind,
        IsSideEffecting = sideEffecting,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    [Theory]
    [InlineData("rerun", true, true)]    // side-effecting node on a rerun → GATE
    [InlineData("rerun", false, false)]  // pure node on a rerun → no gate (re-runs freely)
    [InlineData("manual", true, false)]  // side-effecting node on a normal run → no gate (first-time intent)
    [InlineData("replay", true, false)]  // side-effecting node on a whole-run replay → not a from-node rerun → no gate
    [InlineData(null, true, false)]      // unknown source → no gate
    public void ShouldGate_only_for_a_side_effecting_node_on_a_rerun(string? source, bool sideEffecting, bool expected)
    {
        RerunSideEffectGate.ShouldGate(source, Manifest(sideEffecting)).ShouldBe(expected);
    }

    [Fact]
    public void ShouldGate_uses_the_pinned_rerun_source_constant()
    {
        // Guard the wire coupling: the gate keys on WorkflowRunSourceTypes.Rerun verbatim.
        RerunSideEffectGate.ShouldGate(WorkflowRunSourceTypes.Rerun, Manifest(sideEffecting: true)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"approved":true,"comment":"ok"}""", true)]
    [InlineData("""{"approved":false,"comment":"no"}""", false)]
    [InlineData("""{"comment":"no approved key"}""", false)]   // missing → fail-closed
    [InlineData("""{"approved":"true"}""", false)]              // string, not bool → fail-closed
    [InlineData("""{"approved":1}""", false)]                   // number → fail-closed
    [InlineData("""null""", false)]                             // not an object → fail-closed
    [InlineData("""[]""", false)]                               // array → fail-closed
    public void IsApproved_is_true_only_for_explicit_boolean_true(string payloadJson, bool expected)
    {
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        RerunSideEffectGate.IsApproved(payload).ShouldBe(expected);
    }

    [Fact]
    public void BuildApprovalToken_parks_an_Approval_wait_with_a_per_node_correlation_and_marked_payload()
    {
        var token = RerunSideEffectGate.BuildApprovalToken("openPr", "Open pull request");

        token.Kind.ShouldBe(WorkflowWaitKinds.Approval, "the gate reuses the existing human Approval wait machinery");
        token.CorrelationToken.ShouldBe("rerun-gate::openPr", "per-node token so a multi-side-effect rerun parks independent waits");
        token.Payload.GetProperty("kind").GetString().ShouldBe(RerunSideEffectGate.PayloadKind, "marks the card as a rerun side-effect gate for the UI");
        token.Payload.GetProperty("node").GetString().ShouldBe("openPr");
        token.Payload.GetProperty("message").GetString().ShouldContain("Open pull request");
    }
}
