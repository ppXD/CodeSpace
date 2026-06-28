using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// EF-backed <see cref="ISkillDefinitionService"/>. Mirrors <c>AgentDefinitionService</c> (slug derivation via
/// <see cref="AgentDefinitionService.DeriveSlug"/>, per-team partial-unique slug, soft-delete, race-loss
/// translation). Create/update set only the authorable fields — an imported skill's verbatim frontmatter +
/// pack provenance are left intact, so editing an authored copy never erases re-sync metadata.
/// </summary>
public sealed class SkillDefinitionService : ISkillDefinitionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICustomPackProvider _customPacks;
    private readonly ILogger<SkillDefinitionService> _logger;

    public SkillDefinitionService(CodeSpaceDbContext db, ICustomPackProvider customPacks, ILogger<SkillDefinitionService> logger)
    {
        _db = db;
        _customPacks = customPacks;
        _logger = logger;
    }

    // Lists only WORKING skills — the team's bindable, editable skills (the editor's skill picker). Library STORE
    // snapshots (Scope=Store) are reached through GetAsync, never on this list.
    public async Task<IReadOnlyList<SkillDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken) =>
        await _db.SkillDefinition.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.Scope == DefinitionScope.Working && s.DeletedDate == null)
            .OrderBy(s => s.CreatedDate)
            .Select(s => new SkillDefinitionSummary
            {
                Id = s.Id,
                TeamId = s.TeamId,
                Slug = s.Slug,
                Name = s.Name,
                Description = s.Description,
                Category = s.Category,
                Origin = s.Origin,
                PackId = s.PackId,
                SourceDefinitionId = s.SourceDefinitionId,
                CreatedDate = s.CreatedDate,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<SkillDefinitionDetail?> GetAsync(Guid teamId, Guid skillDefinitionId, CancellationToken cancellationToken) =>
        await _db.SkillDefinition.AsNoTracking()
            .Where(s => s.Id == skillDefinitionId && s.TeamId == teamId && s.DeletedDate == null)
            .Select(s => new SkillDefinitionDetail
            {
                Id = s.Id,
                TeamId = s.TeamId,
                Slug = s.Slug,
                Name = s.Name,
                Description = s.Description,
                Body = s.Body,
                Category = s.Category,
                RawFrontmatterJson = s.RawFrontmatterJson,
                Origin = s.Origin,
                PackId = s.PackId,
                SourceDefinitionId = s.SourceDefinitionId,
                SourcePath = s.SourcePath,
                CreatedDate = s.CreatedDate,
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Guid> CreateAsync(Guid teamId, SkillDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        var slug = DeriveValidSlug(input.Name);

        await EnsureSlugAvailableAsync(teamId, slug, input.Name, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var skill = new SkillDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = SkillDefinitionOrigin.Authored,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplyEditableFields(skill, input);

        _db.SkillDefinition.Add(skill);
        await SaveCreateAsync(skill, slug, input.Name, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Skill created: team={TeamId} skill={SkillId} slug={Slug}", teamId, skill.Id, slug);
        return skill.Id;
    }

    public async Task<Guid> AuthorStoreSkillAsync(Guid teamId, SkillDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        // A hand-authored Library entry: Authored but Scope=Store (it lives in the Library, not the bindable bench)
        // under the team's Custom pack — symmetric with an imported snapshot. No slug-uniqueness check (store handles
        // aren't unique). You instantiate a working copy (InstantiateFromStoreAsync) to bind it to an agent.
        var slug = DeriveValidSlug(input.Name);
        var packId = await _customPacks.EnsureForTeamAsync(teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var skill = new SkillDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = SkillDefinitionOrigin.Authored,
            Scope = DefinitionScope.Store,
            PackId = packId,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplyEditableFields(skill, input);

        _db.SkillDefinition.Add(skill);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);   // flushes the Custom pack (if new) + the store skill atomically

        _logger.LogInformation("Skill authored into Library: team={TeamId} skill={SkillId} slug={Slug} pack={PackId}", teamId, skill.Id, slug, packId);
        return skill.Id;
    }

    public async Task<Guid> InstantiateFromStoreAsync(Guid teamId, Guid sourceSnapshotId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var snapshot = await _db.SkillDefinition.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == sourceSnapshotId && s.TeamId == teamId && s.Scope == DefinitionScope.Store && s.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Store snapshot {sourceSnapshotId} not found or not accessible.");

        var slug = await DeriveAvailableSlugAsync(teamId, snapshot.Name, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var copy = new SkillDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = SkillDefinitionOrigin.Imported,
            Scope = DefinitionScope.Working,
            SourceDefinitionId = snapshot.Id,        // provenance: which snapshot this copy came from
            SourceVersion = snapshot.ContentVersion, // the snapshot version captured at copy time = LHS of a future per-copy sync compare
            // PackId stays NULL — provenance is the source link, so a from-store copy never re-appears in the Library.
            Name = snapshot.Name,
            Description = snapshot.Description,
            Body = snapshot.Body,
            Category = snapshot.Category,
            RawFrontmatterJson = snapshot.RawFrontmatterJson,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };

        _db.SkillDefinition.Add(copy);
        await SaveCreateAsync(copy, slug, snapshot.Name, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Skill instantiated from store: team={TeamId} skill={SkillId} slug={Slug} source={SourceId}", teamId, copy.Id, slug, snapshot.Id);
        return copy.Id;
    }

    public async Task UpdateAsync(Guid teamId, Guid skillDefinitionId, SkillDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("Skill name is required", nameof(input));

        var skill = await LoadActiveAsync(teamId, skillDefinitionId, cancellationToken).ConfigureAwait(false);

        ApplyEditableFields(skill, input);
        skill.LastModifiedDate = DateTimeOffset.UtcNow;
        skill.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Skill updated: team={TeamId} skill={SkillId}", teamId, skillDefinitionId);
    }

    public async Task DeleteAsync(Guid teamId, Guid skillDefinitionId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var skill = await LoadActiveAsync(teamId, skillDefinitionId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        skill.DeletedDate = now;
        skill.LastModifiedDate = now;
        skill.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Skill soft-deleted: team={TeamId} skill={SkillId}", teamId, skillDefinitionId);
    }

    /// <summary>Sets ONLY the authorable fields. Slug, origin, and the import-owned columns (raw frontmatter / pack provenance) are deliberately untouched, so editing an imported skill leaves its re-sync metadata intact.</summary>
    private static void ApplyEditableFields(SkillDefinition skill, SkillDefinitionInput input)
    {
        skill.Name = input.Name;
        skill.Description = input.Description;
        skill.Body = input.Body ?? "";
        skill.Category = NullIfBlank(input.Category);
    }

    private async Task<SkillDefinition> LoadActiveAsync(Guid teamId, Guid skillDefinitionId, CancellationToken cancellationToken)
    {
        var skill = await _db.SkillDefinition
            .Where(s => s.Id == skillDefinitionId && s.TeamId == teamId && s.DeletedDate == null)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return skill ?? throw new KeyNotFoundException($"Skill {skillDefinitionId} not found or not accessible.");
    }

    private async Task EnsureSlugAvailableAsync(Guid teamId, string slug, string requestedName, CancellationToken cancellationToken)
    {
        // Only WORKING handles must be unique (the team-slug index is Working-only). A Library STORE snapshot sharing
        // the handle must NOT block authoring a runnable skill of the same name.
        var exists = await _db.SkillDefinition.AsNoTracking()
            .AnyAsync(s => s.TeamId == teamId && s.Slug == slug && s.Scope == DefinitionScope.Working && s.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (exists) throw SlugTakenError(slug, requestedName, null);
    }

    /// <summary>
    /// The team-unique WORKING handle for a name: the derived slug if free, else the first <c>-2</c>, <c>-3</c>… variant.
    /// Used when instantiating a store skill — picking a library item must not dead-end on a handle a bench skill already
    /// owns. A 50-char probe prefix covers baseSlug AND every trimmed numeric variant (DeriveSlug emits no consecutive
    /// hyphens, so a trim drops at most one char); the exact <c>taken</c> check keeps it precise. Mirrors the agent service.
    /// </summary>
    private async Task<string> DeriveAvailableSlugAsync(Guid teamId, string name, CancellationToken cancellationToken)
    {
        var baseSlug = DeriveValidSlug(name);
        var probe = baseSlug.Length <= 50 ? baseSlug : baseSlug[..50];

        var taken = (await _db.SkillDefinition.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.Scope == DefinitionScope.Working && s.DeletedDate == null && s.Slug.StartsWith(probe))
            .Select(s => s.Slug)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(baseSlug)) return baseSlug;

        for (var n = 2; n < 10000; n++)
        {
            var suffix = $"-{n}";
            var trimmed = baseSlug.Length + suffix.Length <= 64 ? baseSlug : baseSlug[..(64 - suffix.Length)].TrimEnd('-');
            var candidate = trimmed + suffix;

            if (!taken.Contains(candidate)) return candidate;
        }

        throw new InvalidOperationException($"Could not derive a free handle from '{name}' — too many existing variants of '{baseSlug}'.");
    }

    private async Task SaveCreateAsync(SkillDefinition skill, string slug, string requestedName, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsTeamSlugUniqueViolation(ex))
        {
            // Race-loss: two concurrent creates derived the same slug; the partial unique index caught the loser.
            _db.SkillDefinition.Local.Remove(skill);
            throw SlugTakenError(slug, requestedName, ex);
        }
    }

    private static InvalidOperationException SlugTakenError(string slug, string requestedName, Exception? inner) =>
        new($"A skill with handle '{slug}' (derived from name '{requestedName}') already exists in this team. " +
            $"Pick a different name — the handle is how this skill is referenced, and must be unique per team.", inner!);

    private static bool IsTeamSlugUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == "23505"
            && (pg.ConstraintName?.Contains("uq_skill_definition_team_slug", StringComparison.Ordinal) ?? false);

    /// <summary>Derives + validates the handle (reusing the agent slug rule), throwing an actionable error when the name yields nothing usable.</summary>
    private static string DeriveValidSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Skill name is required", nameof(name));

        var slug = AgentDefinitionService.DeriveSlug(name);
        if (slug.Length == 0)
            throw new InvalidOperationException(
                $"Skill name '{name}' has no characters that produce a valid handle. " +
                $"Use a name with at least one letter or digit so we can derive a handle.");

        return slug;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
