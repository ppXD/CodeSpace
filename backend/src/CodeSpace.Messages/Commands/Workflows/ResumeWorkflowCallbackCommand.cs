using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Resume a run parked on <c>flow.wait_callback</c>, presented by an external system at the
/// tokened callback URL. Anonymous — the high-entropy <see cref="Token"/> is the bearer secret;
/// there is no team/user context. Returns false when no pending callback wait matches the token
/// (unknown or already used), which the controller maps to 404.
/// </summary>
public sealed record ResumeWorkflowCallbackCommand : IRequest<bool>
{
    public required string Token { get; init; }

    /// <summary>The raw request body the external system posted. Normalised to JSON by the handler.</summary>
    public string? Body { get; init; }
}
