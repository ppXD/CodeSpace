using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Soft-delete a project-scoped variable. Idempotent. Project ownership is verified against
/// the current team — wrong-team or phantom project → <see cref="KeyNotFoundException"/>.
/// </summary>
public sealed record DeleteProjectVariableCommand : ICommand<Unit>, IRequireTeamMembership
{
    public Guid ProjectId { get; init; }
    public required string Name { get; init; }
}
