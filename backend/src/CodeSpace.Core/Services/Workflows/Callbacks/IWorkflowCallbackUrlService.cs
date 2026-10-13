using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Callbacks;

public interface IWorkflowCallbackUrlService
{
    /// <summary>
    /// The URL of <paramref name="waitId"/>, a pending <c>Callback</c> wait on <paramref name="runId"/> in
    /// <paramref name="teamId"/>, logged as a reveal naming the caller. Throws <see cref="KeyNotFoundException"/> when there
    /// is no such pending callback wait in the team, and <see cref="WorkflowCallbackUrlUnrecoverableException"/> when its
    /// row holds neither a ciphertext nor the token itself.
    /// </summary>
    Task<WorkflowCallbackUrl> RevealAsync(Guid runId, Guid waitId, Guid teamId, CancellationToken cancellationToken);
}
