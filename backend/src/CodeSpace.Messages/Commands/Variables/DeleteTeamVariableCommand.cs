using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Soft-delete a team-scoped variable by name. Idempotent — no-op if the name has no
/// active row. Team comes from <c>X-Team-Id</c> header.
/// </summary>
public sealed record DeleteTeamVariableCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required string Name { get; init; }
}
