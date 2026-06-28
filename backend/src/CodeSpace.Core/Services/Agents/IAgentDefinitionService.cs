using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// CRUD surface for Agent personas (<c>agent_definition</c>) — the team-scoped "Agents library".
/// Handlers are thin Mediator → service dispatchers (Rule 16); slug derivation, uniqueness, the
/// authoring-vs-import field boundary, and tenancy scoping live here.
///
/// <para>Tenant boundary: every method takes <c>teamId</c> (from <c>ICurrentTeam</c> via the handler)
/// and MUST scope every query by it, so a stolen persona id can't read or mutate another team's row.
/// Functional + reusable: takes inputs, returns outputs, no Mediator/ASP.NET coupling — the same
/// service backs a chat @-mention, a recurring re-sync job, and a test.</para>
/// </summary>
public interface IAgentDefinitionService
{
    Task<IReadOnlyList<AgentDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);

    Task<AgentDefinitionSummary?> GetAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken);

    /// <summary>
    /// Author a new persona (<c>Origin = Authored</c>). The slug is derived from <c>input.Name</c> by
    /// <see cref="AgentDefinitionService.DeriveSlug"/>; throws when the name yields no valid slug, and
    /// throws an actionable <see cref="InvalidOperationException"/> when the slug collides with an
    /// existing active persona in the team.
    /// </summary>
    Task<Guid> CreateAsync(Guid teamId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Author a new agent directly INTO the Library — a store entry (<c>Origin=Authored</c>, <c>Scope=Store</c>) under
    /// the team's synthetic Custom pack (created on first use). Symmetric with an imported snapshot: it doesn't land on
    /// the bench, you instantiate a working copy to run it. No slug-uniqueness check (store handles aren't unique).
    /// </summary>
    Task<Guid> AuthorStoreAgentAsync(Guid teamId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Replace the editable surface of an existing persona (full-replace semantics). The slug, origin,
    /// and import-owned fields (skills / MCP / verbatim frontmatter / provenance) are left untouched.
    /// Throws <see cref="KeyNotFoundException"/> when the persona isn't found in this team.
    /// </summary>
    Task UpdateAsync(Guid teamId, Guid agentDefinitionId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Create an IMPORTED persona (<c>Origin = Imported</c>) from a parsed artifact — sets the authorable
    /// fields PLUS the import-owned ones (skills / MCP / verbatim frontmatter / SourcePath / PackId). The
    /// slug is derived from the name and guarded for per-team uniqueness exactly like an authored create
    /// (throws an actionable <see cref="InvalidOperationException"/> on collision).
    /// </summary>
    Task<Guid> ImportAsync(Guid teamId, ImportedAgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Instantiate a new WORKING bench persona by COPYING a Library STORE snapshot's content. The copy is
    /// <c>Origin=Imported</c>, <c>Scope=Working</c>, carries <c>SourceDefinitionId</c> + <c>SourceVersion</c>
    /// provenance (no <c>PackId</c>, so it doesn't re-appear in the Library), and is UNBOUND. The handle is the
    /// snapshot's derived handle, auto-suffixed only on a bench collision — so this never throws on a taken handle.
    /// Throws <see cref="KeyNotFoundException"/> when the snapshot isn't a store row in this team.
    /// </summary>
    Task<Guid> InstantiateFromStoreAsync(Guid teamId, Guid sourceSnapshotId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-delete a persona (the slug becomes reusable). Throws <see cref="KeyNotFoundException"/>
    /// when not found in this team.
    /// </summary>
    Task DeleteAsync(Guid teamId, Guid agentDefinitionId, Guid actorUserId, CancellationToken cancellationToken);
}
