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
public abstract record InteractionTarget;

/// <summary>Resolve the workflow run parked on the Action wait identified by <see cref="Token"/> (see <c>ResumeByActionTokenAsync</c>).</summary>
public sealed record WorkflowWaitTarget : InteractionTarget
{
    public required string Token { get; init; }

    /// <summary>
    /// When set, resolving this wait will make the resumed run act AS the responder on this repository's
    /// provider (e.g. a downstream <c>git.pr_review</c> wired to the responder as <c>actAsUserId</c>). The
    /// respond endpoint enforces — BEFORE resolving the wait — that the responder has a linked identity
    /// there, returning 428 <c>actor_identity_required</c> so the client prompts a link + retries, instead
    /// of the run failing later in the background. Null ⇒ no responder-identity requirement. Server-side
    /// only (carried on the never-surfaced <see cref="MessageInteraction.Target"/>).
    /// </summary>
    public Guid? RequiresResponderIdentityForRepositoryId { get; init; }
}
