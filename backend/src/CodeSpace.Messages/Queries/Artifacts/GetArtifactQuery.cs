using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Artifacts;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Artifacts;

/// <summary>
/// Fetch one artifact's bytes by id. Team-scoped — the team comes from <c>ICurrentTeam</c> (the X-Team-Id header),
/// never the wire (<see cref="IRequireTeamMembership"/>), so a caller only reads its own team's artifacts. A
/// foreign / absent id resolves to <c>null</c> (the handler / controller 404-conflates — existence is never
/// leaked). Read-only. This is the read surface behind <c>AgentRunResult.PatchArtifactId</c> (D2): when a large
/// diff was offloaded, the consumer fetches the full diff here.
/// </summary>
public sealed record GetArtifactQuery : IQuery<ArtifactDownload?>, IRequireTeamMembership
{
    public required Guid ArtifactId { get; init; }
}
