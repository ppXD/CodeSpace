using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Slugs;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IAgentDefinitionService"/>. Personas are simple rows; the logic worth owning is
/// slug derivation + per-team uniqueness, the authoring-vs-import field boundary (create/update never
/// touch skills / MCP / verbatim frontmatter / provenance), and tenancy scoping.
/// </summary>
public sealed class AgentDefinitionService : IAgentDefinitionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICustomPackProvider _customPacks;
    private readonly ILogger<AgentDefinitionService> _logger;

    public AgentDefinitionService(CodeSpaceDbContext db, ICustomPackProvider customPacks, ILogger<AgentDefinitionService> logger)
    {
        _db = db;
        _customPacks = customPacks;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        // The bench lists only WORKING personas — the @-mentionable, runnable agents. Store snapshots (Origin=Imported,
        // Scope=Store) live in the Library and are reached through GetAsync via the editor, never on this list.
        var rows = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.Scope == DefinitionScope.Working && a.DeletedDate == null)
            .OrderBy(a => a.CreatedDate)
            .Select(SummaryProjection())
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var boundSkills = await LoadBoundSkillsAsync(teamId, rows.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var packNames = await LoadPackNamesAsync(teamId, rows.Where(r => r.PackId != null).Select(r => r.PackId!.Value).ToList(), cancellationToken).ConfigureAwait(false);

        return rows.Select(r => ToSummary(r) with
        {
            BoundSkills = boundSkills.GetValueOrDefault(r.Id, Array.Empty<AgentBoundSkill>()),
            PackName = r.PackId == null ? null : packNames.GetValueOrDefault(r.PackId.Value),
        }).ToList();
    }

    public async Task<AgentDefinitionSummary?> GetAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var row = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.Id == agentDefinitionId && a.TeamId == teamId && a.DeletedDate == null)
            .Select(SummaryProjection())
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null) return null;

        var boundSkills = await LoadBoundSkillsAsync(teamId, new[] { row.Id }, cancellationToken).ConfigureAwait(false);
        var packNames = row.PackId == null ? null : await LoadPackNamesAsync(teamId, new[] { row.PackId.Value }, cancellationToken).ConfigureAwait(false);

        return ToSummary(row) with
        {
            BoundSkills = boundSkills.GetValueOrDefault(row.Id, Array.Empty<AgentBoundSkill>()),
            PackName = row.PackId == null ? null : packNames!.GetValueOrDefault(row.PackId.Value),
        };
    }

    /// <summary>The skills bound to each of <paramref name="agentIds"/>, via the AgentSkillBinding join through to ACTIVE skills in <paramref name="teamId"/>, ordered by handle — one batched query, no N+1. The skill side is team-scoped as defense-in-depth (matching <c>AgentDefinitionResolver.LoadSkillsAsync</c>): the binding writer already enforces same-team, but the read never trusts a stray row. Agents with no bindings are simply absent from the map.</summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AgentBoundSkill>>> LoadBoundSkillsAsync(Guid teamId, IReadOnlyCollection<Guid> agentIds, CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<AgentBoundSkill>>();

        var rows = await (from b in _db.AgentSkillBinding.AsNoTracking()
                          join s in _db.SkillDefinition.AsNoTracking() on b.SkillDefinitionId equals s.Id
                          where agentIds.Contains(b.AgentDefinitionId) && s.TeamId == teamId && s.DeletedDate == null
                          orderby s.Slug
                          select new { b.AgentDefinitionId, s.Id, s.Slug, s.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.AgentDefinitionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AgentBoundSkill>)g.Select(r => new AgentBoundSkill { SkillDefinitionId = r.Id, Slug = r.Slug, Name = r.Name }).ToList());
    }

    /// <summary>The pack name (owner/repo) for each of <paramref name="packIds"/>, team-scoped — one batched query so the bench list never N+1s a pack lookup per imported persona.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadPackNamesAsync(Guid teamId, IReadOnlyCollection<Guid> packIds, CancellationToken cancellationToken)
    {
        if (packIds.Count == 0) return new Dictionary<Guid, string>();

        return await _db.Pack.AsNoTracking()
            .Where(p => p.TeamId == teamId && packIds.Contains(p.Id) && p.DeletedDate == null)
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> CreateAsync(Guid teamId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        var slug = DeriveValidSlug(input.Name);

        await EnsureSlugAvailableAsync(teamId, slug, input.Name, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = AgentDefinitionOrigin.Authored,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplyEditableFields(agent, input);

        _db.AgentDefinition.Add(agent);
        await SaveCreateAsync(agent, slug, input.Name, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent persona created: team={TeamId} agent={AgentId} slug={Slug}", teamId, agent.Id, slug);
        return agent.Id;
    }

    public async Task<Guid> AuthorStoreAgentAsync(Guid teamId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        // A hand-authored Library entry: Authored (the operator wrote it) but Scope=Store (it lives in the Library,
        // not on the bench) under the team's Custom pack — symmetric with an imported snapshot. No slug-uniqueness
        // check: store handles aren't unique (the team-slug index is Working-only), so it never collides. You
        // instantiate a working copy (InstantiateFromStoreAsync) to run it.
        var slug = DeriveValidSlug(input.Name);
        var packId = await _customPacks.EnsureForTeamAsync(teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = AgentDefinitionOrigin.Authored,
            Scope = DefinitionScope.Store,
            PackId = packId,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplyEditableFields(agent, input);

        _db.AgentDefinition.Add(agent);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);   // flushes the Custom pack (if new) + the store agent atomically

        _logger.LogInformation("Agent authored into Library: team={TeamId} agent={AgentId} slug={Slug} pack={PackId}", teamId, agent.Id, slug, packId);
        return agent.Id;
    }

    public async Task<Guid> ImportAsync(Guid teamId, ImportedAgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        var slug = DeriveValidSlug(input.Name);

        await EnsureSlugAvailableAsync(teamId, slug, input.Name, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = AgentDefinitionOrigin.Imported,
            PackId = input.PackId,
            SourcePath = input.SourcePath,
            Name = input.Name,
            Description = input.Description,
            SystemPrompt = input.SystemPrompt ?? "",
            Model = NullIfBlank(input.Model),
            DefaultAutonomy = NullIfBlank(input.DefaultAutonomy),
            ToolsJson = SerializeTools(input.Tools),
            McpServersJson = string.IsNullOrWhiteSpace(input.McpServersJson) ? "[]" : input.McpServersJson,
            RawFrontmatterJson = string.IsNullOrWhiteSpace(input.RawFrontmatterJson) ? "{}" : input.RawFrontmatterJson,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };

        _db.AgentDefinition.Add(agent);
        await SaveCreateAsync(agent, slug, input.Name, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent persona imported: team={TeamId} agent={AgentId} slug={Slug} source={SourcePath}", teamId, agent.Id, slug, input.SourcePath);
        return agent.Id;
    }

    public async Task<Guid> InstantiateFromStoreAsync(Guid teamId, Guid sourceSnapshotId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var snapshot = await _db.AgentDefinition.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == sourceSnapshotId && a.TeamId == teamId && a.Scope == DefinitionScope.Store && a.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Store snapshot {sourceSnapshotId} not found or not accessible.");

        var slug = await DeriveAvailableSlugAsync(teamId, snapshot.Name, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var copy = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = AgentDefinitionOrigin.Imported,
            Scope = DefinitionScope.Working,
            SourceDefinitionId = snapshot.Id,        // provenance: which snapshot this copy came from
            SourceVersion = snapshot.ContentVersion, // the snapshot version captured at copy time = LHS of a future per-copy sync compare
            // PackId stays NULL on purpose — provenance is the source link, so a from-store copy never re-appears in the Library.
            Name = snapshot.Name,
            Description = snapshot.Description,
            SystemPrompt = snapshot.SystemPrompt,
            Model = snapshot.Model,
            DefaultAutonomy = snapshot.DefaultAutonomy,
            ToolsJson = snapshot.ToolsJson,
            McpServersJson = snapshot.McpServersJson,
            RawFrontmatterJson = snapshot.RawFrontmatterJson,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };

        _db.AgentDefinition.Add(copy);
        await SaveCreateAsync(copy, slug, snapshot.Name, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent persona instantiated from store: team={TeamId} agent={AgentId} slug={Slug} source={SourceId}", teamId, copy.Id, slug, snapshot.Id);
        return copy.Id;
    }

    public async Task UpdateAsync(Guid teamId, Guid agentDefinitionId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("Agent name is required", nameof(input));

        var agent = await LoadActiveAsync(teamId, agentDefinitionId, cancellationToken).ConfigureAwait(false);

        ApplyEditableFields(agent, input);
        agent.LastModifiedDate = DateTimeOffset.UtcNow;
        agent.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Agent persona updated: team={TeamId} agent={AgentId}", teamId, agentDefinitionId);
    }

    public async Task DeleteAsync(Guid teamId, Guid agentDefinitionId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var agent = await LoadActiveAsync(teamId, agentDefinitionId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        agent.DeletedDate = now;
        agent.LastModifiedDate = now;
        agent.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Agent persona soft-deleted: team={TeamId} agent={AgentId}", teamId, agentDefinitionId);
    }

    /// <summary>
    /// Sets ONLY the authorable fields. Slug, origin, and the import-owned columns (skills / MCP /
    /// raw frontmatter / pack provenance) are deliberately not assigned here — editing an imported
    /// persona's prompt must leave its imported skills + re-sync provenance intact.
    /// </summary>
    private static void ApplyEditableFields(AgentDefinition agent, AgentDefinitionInput input)
    {
        agent.Name = input.Name;
        agent.Description = input.Description;
        agent.SystemPrompt = input.SystemPrompt ?? "";
        agent.Model = NullIfBlank(input.Model);
        agent.DefaultAutonomy = NullIfBlank(input.DefaultAutonomy);
        agent.ToolsJson = SerializeTools(input.Tools);
    }

    private async Task<AgentDefinition> LoadActiveAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var agent = await _db.AgentDefinition
            .Where(a => a.Id == agentDefinitionId && a.TeamId == teamId && a.DeletedDate == null)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return agent ?? throw new KeyNotFoundException($"Agent persona {agentDefinitionId} not found or not accessible.");
    }

    private async Task EnsureSlugAvailableAsync(Guid teamId, string slug, string requestedName, CancellationToken cancellationToken)
    {
        // Only WORKING handles must be unique — that's what the team-slug index enforces. A Library STORE snapshot may
        // share the handle, so it must NOT block authoring/importing a runnable persona of the same name.
        var exists = await _db.AgentDefinition.AsNoTracking()
            .AnyAsync(a => a.TeamId == teamId && a.Slug == slug && a.Scope == DefinitionScope.Working && a.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (exists) throw SlugTakenError(slug, requestedName, null);
    }

    /// <summary>
    /// The team-unique WORKING handle for a name: the derived slug if free, else the first <c>-2</c>, <c>-3</c>… variant.
    /// Used when instantiating a store snapshot — picking a library item must not dead-end on a handle a bench persona
    /// already owns (unlike authoring, where the operator typed the name and fixes a collision themselves). The 64-char
    /// cap is honoured by trimming the base so the numeric suffix always fits. A concurrent claim of the chosen variant
    /// still surfaces as the typed slug-collision error via <see cref="SaveCreateAsync"/> (a rare retry).
    /// </summary>
    private async Task<string> DeriveAvailableSlugAsync(Guid teamId, string name, CancellationToken cancellationToken)
    {
        var baseSlug = DeriveValidSlug(name);

        var taken = (await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.Scope == DefinitionScope.Working && a.DeletedDate == null && a.Slug.StartsWith(SlugDeduper.ProbePrefix(baseSlug)))
            .Select(a => a.Slug)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        return SlugDeduper.DeriveAvailable(baseSlug, taken);
    }

    private async Task SaveCreateAsync(AgentDefinition agent, string slug, string requestedName, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsTeamSlugUniqueViolation(ex))
        {
            // Race-loss: two concurrent creates derived the same slug; the pre-check passed for both
            // before either write landed and the partial unique index caught the loser. Translate to
            // the same friendly "pick a different name" error the pre-check emits.
            _db.AgentDefinition.Local.Remove(agent);
            throw SlugTakenError(slug, requestedName, ex);
        }
    }

    private static InvalidOperationException SlugTakenError(string slug, string requestedName, Exception? inner) =>
        new($"An agent with handle '{slug}' (derived from name '{requestedName}') already exists in this team. " +
            $"Pick a different name — the handle is how this agent is @-mentioned and referenced, and must be unique per team.", inner!);

    private static bool IsTeamSlugUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == "23505"
            && (pg.ConstraintName?.Contains("uq_agent_definition_team_slug", StringComparison.Ordinal) ?? false);

    /// <summary>Derives + validates the @-mention handle, throwing an actionable error when the name yields nothing usable.</summary>
    private static string DeriveValidSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Agent name is required", nameof(name));

        var slug = DeriveSlug(name);
        if (slug.Length == 0)
            throw new InvalidOperationException(
                $"Agent name '{name}' has no characters that produce a valid handle. " +
                $"Use a name with at least one letter or digit so we can derive an @-mention handle.");

        return slug;
    }

    /// <summary>
    /// Derives the kebab-case @-mention handle from a name: lowercase, ASCII <c>[a-z0-9_]</c> kept,
    /// every other run collapses to a single hyphen, leading/trailing hyphens trimmed, capped at 64.
    /// Returns empty when no character survives (the caller throws). Public + static so it's unit-tested
    /// directly and reused by the import slice for authored-style handles.
    /// </summary>
    public static string DeriveSlug(string name) => Slug.Slugify(name);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? SerializeTools(IReadOnlyList<string>? tools) =>
        tools == null ? null : JsonSerializer.Serialize(tools, AgentJson.Options);

    private static IReadOnlyList<string>? ParseTools(string? toolsJson) =>
        string.IsNullOrWhiteSpace(toolsJson) ? null : JsonSerializer.Deserialize<List<string>>(toolsJson, AgentJson.Options);

    /// <summary>Selects only the summary columns (skips the potentially large skills / MCP / frontmatter blobs).</summary>
    private static System.Linq.Expressions.Expression<Func<AgentDefinition, SummaryRow>> SummaryProjection() =>
        a => new SummaryRow
        {
            Id = a.Id,
            TeamId = a.TeamId,
            Slug = a.Slug,
            Name = a.Name,
            Description = a.Description,
            SystemPrompt = a.SystemPrompt,
            Model = a.Model,
            DefaultAutonomy = a.DefaultAutonomy,
            ToolsJson = a.ToolsJson,
            Origin = a.Origin,
            PackId = a.PackId,
            CreatedDate = a.CreatedDate,
        };

    private static AgentDefinitionSummary ToSummary(SummaryRow r) => new()
    {
        Id = r.Id,
        TeamId = r.TeamId,
        Slug = r.Slug,
        Name = r.Name,
        Description = r.Description,
        SystemPrompt = r.SystemPrompt,
        Model = r.Model,
        DefaultAutonomy = r.DefaultAutonomy,
        Tools = ParseTools(r.ToolsJson),
        Origin = r.Origin,
        CreatedDate = r.CreatedDate,
    };

    /// <summary>Flat row shape for the summary projection — keeps tools as raw JSON until the in-memory map deserializes it.</summary>
    private sealed class SummaryRow
    {
        public Guid Id { get; init; }
        public Guid TeamId { get; init; }
        public string Slug { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string? Description { get; init; }
        public string SystemPrompt { get; init; } = "";
        public string? Model { get; init; }
        public string? DefaultAutonomy { get; init; }
        public string? ToolsJson { get; init; }
        public AgentDefinitionOrigin Origin { get; init; }
        public Guid? PackId { get; init; }
        public DateTimeOffset CreatedDate { get; init; }
    }
}
