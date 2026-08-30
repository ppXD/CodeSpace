using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

/// <summary>Rule 16 — thin handler. The bounded recovery pass lives in <see cref="IArtifactCasTransferResumer"/>.</summary>
public sealed class ResumeAbandonedArtifactTransfersCommandHandler : IRequestHandler<ResumeAbandonedArtifactTransfersCommand, ResumeAbandonedArtifactTransfersResponse>
{
    /// <summary>Bounded per pass so one tick cannot spend an unbounded number of provider round trips; oldest abandonment first means a deployment still converges on finishing everything.</summary>
    internal const int BatchSize = 50;

    private readonly IArtifactCasTransferResumer _resumer;

    public ResumeAbandonedArtifactTransfersCommandHandler(IArtifactCasTransferResumer resumer) { _resumer = resumer; }

    public async Task<ResumeAbandonedArtifactTransfersResponse> Handle(ResumeAbandonedArtifactTransfersCommand request, CancellationToken cancellationToken)
    {
        var summary = await _resumer.ResumeAbandonedAsync(BatchSize, cancellationToken).ConfigureAwait(false);

        return new ResumeAbandonedArtifactTransfersResponse
        {
            Examined = summary.Examined, Committed = summary.Committed, Settled = summary.Settled,
            Orphaned = summary.Orphaned, Inconclusive = summary.Inconclusive, Contended = summary.Contended,
        };
    }
}
