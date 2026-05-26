using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

public sealed record DeleteProjectVariableCommand : IRequest<Unit>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
}
