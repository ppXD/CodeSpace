using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Soft-delete a persona. The row stays for audit + to keep its run history intact; its slug becomes
/// free to reuse (the unique index is partial on non-deleted rows). Idempotent re-runs after delete
/// throw not-found.
/// </summary>
public sealed record DeleteAgentDefinitionCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid AgentDefinitionId { get; init; }
}
