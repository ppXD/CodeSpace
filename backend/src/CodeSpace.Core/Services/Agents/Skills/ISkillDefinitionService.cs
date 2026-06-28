using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// Manages a team's skill library (the "skill store") — authored skills today, pack-imported ones added by the
/// importer slice. The skill peer of <c>IAgentDefinitionService</c>: slug derivation + per-team uniqueness,
/// the authoring-vs-import field boundary (create/update never touch verbatim frontmatter / provenance), and
/// tenancy scoping. List is Level-1 (no body); Get is Level-2 (body included).
/// </summary>
public interface ISkillDefinitionService
{
    Task<IReadOnlyList<SkillDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);

    Task<SkillDefinitionDetail?> GetAsync(Guid teamId, Guid skillDefinitionId, CancellationToken cancellationToken);

    Task<Guid> CreateAsync(Guid teamId, SkillDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Instantiate a new WORKING (bindable) skill by COPYING a Library STORE snapshot's content — the skill analogue of
    /// <c>AgentDefinitionService.InstantiateFromStoreAsync</c>. The copy is <c>Origin=Imported</c>, <c>Scope=Working</c>,
    /// carries <c>SourceDefinitionId</c> + <c>SourceVersion</c> provenance (no <c>PackId</c>, so it doesn't re-appear in
    /// the Library), with a team-unique handle auto-suffixed only on a bench collision. Throws <see cref="KeyNotFoundException"/>
    /// when the snapshot isn't a store row in this team.
    /// </summary>
    Task<Guid> InstantiateFromStoreAsync(Guid teamId, Guid sourceSnapshotId, Guid actorUserId, CancellationToken cancellationToken);

    Task UpdateAsync(Guid teamId, Guid skillDefinitionId, SkillDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid teamId, Guid skillDefinitionId, Guid actorUserId, CancellationToken cancellationToken);
}
