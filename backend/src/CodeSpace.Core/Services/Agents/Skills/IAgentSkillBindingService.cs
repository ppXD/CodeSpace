using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// Manages the many-to-many binding between Agent personas and Skills (the <c>AgentSkillBinding</c> join).
/// Both directions are first-class index-backed reads: the skills an agent carries, and the agents that use a
/// skill. <see cref="SetForAgentAsync"/> replaces an agent's whole skill set transactionally (the editor's
/// "these are my skills now" save). All ops are team-scoped and validate that both sides are active members
/// of the team.
/// </summary>
public interface IAgentSkillBindingService
{
    /// <summary>The active skills bound to an agent (Level-1 summaries), ordered by bind time.</summary>
    Task<IReadOnlyList<SkillDefinitionSummary>> ListForAgentAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken);

    /// <summary>Reverse lookup: the active agents that carry a given skill.</summary>
    Task<IReadOnlyList<AgentDefinitionSummary>> ListAgentsForSkillAsync(Guid teamId, Guid skillDefinitionId, CancellationToken cancellationToken);

    /// <summary>Replaces the agent's skill set with exactly <paramref name="skillDefinitionIds"/> (add missing, remove dropped, leave unchanged). Throws when the agent or any skill is not an active member of the team.</summary>
    Task SetForAgentAsync(Guid teamId, Guid agentDefinitionId, IReadOnlyList<Guid> skillDefinitionIds, Guid actorUserId, CancellationToken cancellationToken);
}
