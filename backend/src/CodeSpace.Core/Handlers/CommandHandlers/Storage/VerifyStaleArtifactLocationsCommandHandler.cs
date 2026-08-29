using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class VerifyStaleArtifactLocationsCommandHandler : IRequestHandler<VerifyStaleArtifactLocationsCommand, VerifyStaleArtifactLocationsResponse>
{
    /// <summary>Bounded per pass so one tick cannot spend an unbounded number of provider round trips; the least-recently-verified ordering means a deployment still converges on checking everything.</summary>
    internal const int BatchSize = 100;

    private readonly IArtifactLocationVerifier _verifier;

    public VerifyStaleArtifactLocationsCommandHandler(IArtifactLocationVerifier verifier) { _verifier = verifier; }

    public async Task<VerifyStaleArtifactLocationsResponse> Handle(VerifyStaleArtifactLocationsCommand request, CancellationToken cancellationToken)
    {
        var summary = await _verifier.VerifyStaleAsync(BatchSize, cancellationToken).ConfigureAwait(false);

        return new VerifyStaleArtifactLocationsResponse
        {
            Checked = summary.Checked, Confirmed = summary.Confirmed,
            Missing = summary.Missing, Corrupt = summary.Corrupt, Inconclusive = summary.Inconclusive,
        };
    }
}
