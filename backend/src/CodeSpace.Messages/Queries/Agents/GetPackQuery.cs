using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>One pack with its agents + skills (the store detail pane). Null when the pack doesn't exist in the caller's team.</summary>
public sealed record GetPackQuery : IRequest<PackDetail?>, IRequireTeamMembership
{
    public required Guid PackId { get; init; }
}
