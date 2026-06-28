using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Soft-delete a skill. The row stays for audit; its slug frees up for reuse (the unique index is partial on
/// non-deleted rows) and any agent that bound it silently drops it (the binding read joins through to active
/// skills). Idempotent re-runs after delete throw not-found.
/// </summary>
public sealed record DeleteSkillCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid SkillDefinitionId { get; init; }
}
