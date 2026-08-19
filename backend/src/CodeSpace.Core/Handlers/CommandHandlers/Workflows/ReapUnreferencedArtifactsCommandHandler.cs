using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Rule 16 — thin handler. The bounded sweep lives in <see cref="IArtifactRetentionReaper"/>.</summary>
public sealed class ReapUnreferencedArtifactsCommandHandler : IRequestHandler<ReapUnreferencedArtifactsCommand, ReapUnreferencedArtifactsResponse>
{
    private readonly IArtifactRetentionReaper _reaper;

    public ReapUnreferencedArtifactsCommandHandler(IArtifactRetentionReaper reaper) { _reaper = reaper; }

    public async Task<ReapUnreferencedArtifactsResponse> Handle(ReapUnreferencedArtifactsCommand request, CancellationToken cancellationToken)
    {
        var summary = await _reaper.SweepAsync(cancellationToken).ConfigureAwait(false);

        return new ReapUnreferencedArtifactsResponse { Summary = summary };
    }
}
