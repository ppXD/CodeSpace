using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// Returns every node type currently loaded into the engine — feeds the editor's left-rail
/// palette. Schemas come back as JsonElement so the frontend can render config forms directly.
/// </summary>
public sealed record ListNodeManifestsQuery : IQuery<IReadOnlyList<NodeManifestDto>>, IRequireAuthenticatedUser
{
}
