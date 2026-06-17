using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only node that is BOTH <c>IsSideEffecting</c> AND <c>CanSuspend</c> — the hermetic stand-in for the one
/// real such node (<c>chat.post_message</c> with waitForResponse). It pins the from-node rerun precedence: a
/// node with both flags is REFUSED at staging via the CanSuspend (rerun-unsupported) arm and never reaches the
/// runtime side-effect approval gate (fail-closed wins). In the refusal test it never executes; its RunAsync
/// parks an Action wait so it is a valid suspendable node if ever run.
///
/// <para>D7-5: this node ALSO pins the map-branch BODY refusal — <c>RerunBranchBodyPolicy</c> refuses it via its
/// DISTINCT both-flag arm (<c>IsSideEffecting &amp;&amp; CanSuspend</c>), independently of the suspend arm, because the
/// D7-3 gate cannot compose with the node's own post-then-suspend. So it now guards TWO independent refusal arms
/// (from-node <c>IsRerunUnsupported</c> AND map-branch <c>RerunBranchBodyPolicy</c>); relaxing either policy would
/// turn its tests RED.</para>
///
/// <para>Registered into the integration container via <c>PostgresFixture.RegisterTestAssemblyTypes</c> as an
/// <c>INodeRuntime</c>; NOT in any IPluginModule, so it never reaches the editor palette.</para>
/// </summary>
public sealed class BothFlagsProbeNode : INodeRuntime
{
    public const string Key = "test.both_flags_probe";

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Side-effecting + suspendable (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        IsSideEffecting = true,
        CanSuspend = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (context.ResumePayload.HasValue)
            return Task.FromResult(NodeResult.Ok());

        var token = new SuspensionToken
        {
            Kind = WorkflowWaitKinds.Action,
            Payload = JsonSerializer.SerializeToElement(new { kind = "both_flags_probe" }),
            CorrelationToken = "both-flags",
        };
        return Task.FromResult(NodeResult.Suspend(token));
    }
}
