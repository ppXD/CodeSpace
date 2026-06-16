using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Dtos.Artifacts;
using CodeSpace.Messages.Queries.Artifacts;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Artifacts;

/// <summary>
/// Thin dispatcher (Rule 16): scopes to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire), fetches
/// the artifact bytes through <see cref="IArtifactStore"/> (which transparently resolves inline vs. offloaded
/// storage), and maps to the download DTO. A foreign / absent id → the store returns null → this returns null →
/// the controller 404-conflates (no existence leak). No DbContext, no business logic here.
/// </summary>
public sealed class GetArtifactQueryHandler : IRequestHandler<GetArtifactQuery, ArtifactDownload?>
{
    private readonly IArtifactStore _artifacts;
    private readonly ICurrentTeam _currentTeam;

    public GetArtifactQueryHandler(IArtifactStore artifacts, ICurrentTeam currentTeam)
    {
        _artifacts = artifacts;
        _currentTeam = currentTeam;
    }

    public async Task<ArtifactDownload?> Handle(GetArtifactQuery request, CancellationToken cancellationToken)
    {
        var bytes = await _artifacts.GetBytesAsync(_currentTeam.Id!.Value, request.ArtifactId, cancellationToken).ConfigureAwait(false);

        if (bytes == null) return null;

        return new ArtifactDownload { Id = bytes.Id, ContentType = bytes.ContentType, Bytes = bytes.Bytes };
    }
}
