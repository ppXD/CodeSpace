using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// Single workflow read keyed by a URL reference — either a GUID (legacy link) or the team-unique
/// slug (canonical clean URL). Returns null on miss / not-yours. The router uses the returned
/// <c>Slug</c> to canonicalise a legacy-GUID URL to the slug URL.
/// </summary>
public sealed record GetWorkflowByRefQuery : IRequest<WorkflowDetail?>, IRequireTeamMembership
{
    public required string IdOrSlug { get; init; }
}
