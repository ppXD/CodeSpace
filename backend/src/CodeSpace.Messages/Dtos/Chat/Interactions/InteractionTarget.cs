using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// Where a response to an interactive message ROUTES — polymorphic, discriminated by <c>kind</c>.
/// Today the only target is <see cref="WorkflowWaitTarget"/> (resolve a parked workflow run); a
/// future <c>webhook</c> / <c>internal_command</c> target slots in as another
/// <see cref="JsonDerivedTypeAttribute"/> without touching the message model.
///
/// <para>The target is SERVER-SIDE ONLY — it carries the wait token (a bearer secret). It is part of
/// the stored <see cref="MessageInteraction"/> but is deliberately omitted from
/// <see cref="MessageInteractionView"/>, so the token never reaches a client.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WorkflowWaitTarget), "workflow_wait")]
[JsonDerivedType(typeof(ToolCallApprovalTarget), "tool_call_approval")]
[JsonDerivedType(typeof(DecisionRequestTarget), "decision_request")]
public abstract record InteractionTarget;

/// <summary>Resolve the workflow run parked on the Action wait identified by <see cref="Token"/> (see <c>ResumeByActionTokenAsync</c>).</summary>
public sealed record WorkflowWaitTarget : InteractionTarget
{
    public required string Token { get; init; }
}

/// <summary>Record the human's approve/reject decision on the parked tool-call approval whose <see cref="Token"/> matches <c>ToolCallLedger.ApprovalToken</c> (server-side-only, omitted from <see cref="MessageInteractionView"/> exactly like <see cref="WorkflowWaitTarget"/>).</summary>
public sealed record ToolCallApprovalTarget : InteractionTarget
{
    public required string Token { get; init; }
}

/// <summary>
/// Record the human's TYPED answer to a parked agent-grain <c>decision.request</c> (Decision substrate D2) whose
/// <see cref="Token"/> matches <c>ToolCallLedger.ApprovalToken</c>. The same durable spine as
/// <see cref="ToolCallApprovalTarget"/> (the decision parks as a tool-ledger row), but the clicked button's key is an
/// OPTION id (or the free-text sentinel) rather than approve/reject — the resolver builds a <c>DecisionAnswer</c> from
/// it. Server-side-only (carries the bearer token), omitted from <see cref="MessageInteractionView"/>.
/// </summary>
public sealed record DecisionRequestTarget : InteractionTarget
{
    public required string Token { get; init; }
}
