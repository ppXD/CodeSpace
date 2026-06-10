using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
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
    private readonly ILogger<AgentDefinitionService> _logger;

    public AgentDefinitionService(CodeSpaceDbContext db, ILogger<AgentDefinitionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.DeletedDate == null)
            .OrderBy(a => a.CreatedDate)
            .Select(SummaryProjection())
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(ToSummary).ToList();
    }

    public async Task<AgentDefinitionSummary?> GetAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var row = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.Id == agentDefinitionId && a.TeamId == teamId && a.DeletedDate == null)
            .Select(SummaryProjection())
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return row == null ? null : ToSummary(row);
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
        var exists = await _db.AgentDefinition.AsNoTracking()
            .AnyAsync(a => a.TeamId == teamId && a.Slug == slug && a.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (exists) throw SlugTakenError(slug, requestedName, null);
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
    public static string DeriveSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var sb = new System.Text.StringBuilder(name.Length);
        var lastWasHyphen = true;   // suppresses a leading hyphen
        foreach (var c in name)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var result = sb.ToString().TrimEnd('-');
        return result.Length <= 64 ? result : result[..64].TrimEnd('-');
    }

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
        public DateTimeOffset CreatedDate { get; init; }
    }
}
