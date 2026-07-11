using System.Linq.Expressions;
using System.Text.RegularExpressions;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Slugs;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Projects;

/// <summary>
/// EF-backed <see cref="IProjectService"/>. Project rows are simple — slug + name +
/// description + soft-delete + audit — but the cascade-on-delete of project-scoped
/// variables + the "must have a default" invariant live here.
/// </summary>
public sealed class ProjectService : IProjectService, IScopedDependency
{
    /// <summary>
    /// Slug shape — pinned by Rule 8 because it's part of the variable-path contract:
    /// changing the regex retroactively invalidates every <c>{{project.{Slug}.X}}</c>
    /// reference. <c>^[A-Za-z0-9_-]{1,64}$</c> matches the DB CHECK exactly.
    /// </summary>
    public const string SlugPattern = "^[A-Za-z0-9_-]{1,64}$";
    public const string DefaultProjectSlug = "default";

    private static readonly Regex SlugRegex = new(SlugPattern, RegexOptions.Compiled);

    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(CodeSpaceDbContext db, ILogger<ProjectService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.Project.AsNoTracking()
            .Where(p => p.TeamId == teamId && p.DeletedDate == null)
            .OrderBy(p => p.CreatedDate)
            .Select(p => new
            {
                p.Id,
                p.TeamId,
                p.Slug,
                p.Name,
                p.Description,
                p.CreatedDate,
                ActiveRepositoryCount = _db.ProjectRepository.Count(pr => pr.ProjectId == p.Id && pr.DeletedDate == null),
                ActiveVariableCount = _db.Variable.Count(v => v.Scope == VariableScope.Project && v.ScopeId == p.Id && v.DeletedDate == null),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new ProjectSummary
        {
            Id = r.Id,
            TeamId = r.TeamId,
            Slug = r.Slug,
            Name = r.Name,
            Description = r.Description,
            CreatedDate = r.CreatedDate,
            ActiveRepositoryCount = r.ActiveRepositoryCount,
            ActiveVariableCount = r.ActiveVariableCount,
        }).ToList();
    }

    public Task<ProjectSummary?> GetAsync(Guid teamId, Guid projectId, CancellationToken cancellationToken) =>
        FindAsync(teamId, p => p.Id == projectId, cancellationToken);

    public async Task<ProjectSummary?> GetByRefAsync(Guid teamId, string idOrSlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug)) return null;

        // Slug FIRST, then a GUID fallback: a slug can be GUID-shaped (the CHECK allows 32 hex), so a
        // GUID-first branch would look up a random id and 404 a real row. Only a ref that matches no slug
        // AND parses as a GUID falls through to the legacy-id lookup — keeping old GUID links working.
        var bySlug = await FindAsync(teamId, p => p.Slug == idOrSlug, cancellationToken).ConfigureAwait(false);
        if (bySlug != null) return bySlug;

        return Guid.TryParse(idOrSlug, out var id)
            ? await FindAsync(teamId, p => p.Id == id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Single shared read + projection for both by-GUID and by-slug lookups. Always scopes by
    /// team_id + alive so a stolen id/slug can't cross the tenant boundary; <paramref name="match"/>
    /// carries the id-vs-slug predicate on top.
    /// </summary>
    private async Task<ProjectSummary?> FindAsync(Guid teamId, Expression<Func<Project, bool>> match, CancellationToken cancellationToken)
    {
        var row = await _db.Project.AsNoTracking()
            .Where(p => p.TeamId == teamId && p.DeletedDate == null)
            .Where(match)
            .Select(p => new
            {
                p.Id,
                p.TeamId,
                p.Slug,
                p.Name,
                p.Description,
                p.CreatedDate,
                ActiveRepositoryCount = _db.ProjectRepository.Count(pr => pr.ProjectId == p.Id && pr.DeletedDate == null),
                ActiveVariableCount = _db.Variable.Count(v => v.Scope == VariableScope.Project && v.ScopeId == p.Id && v.DeletedDate == null),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null) return null;

        return new ProjectSummary
        {
            Id = row.Id,
            TeamId = row.TeamId,
            Slug = row.Slug,
            Name = row.Name,
            Description = row.Description,
            CreatedDate = row.CreatedDate,
            ActiveRepositoryCount = row.ActiveRepositoryCount,
            ActiveVariableCount = row.ActiveVariableCount,
        };
    }

    public async Task<Guid> CreateAsync(Guid teamId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required", nameof(name));

        var slug = SlugifyName(name);
        if (slug.Length == 0)
            throw new InvalidOperationException(
                $"Project name '{name}' has no characters that produce a valid slug. " +
                $"Use a name with at least one letter or digit so we can derive an identifier for variable paths.");

        EnsureValidSlug(slug);
        await EnsureSlugAvailableAsync(teamId, slug, name, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Name = name,
            Description = description,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };

        _db.Project.Add(project);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsTeamSlugUniqueViolation(ex))
        {
            // Race-loss: two concurrent callers derived the same slug from different
            // names ("Acme Backend" + "acme backend"). EnsureSlugAvailableAsync's
            // pre-check passed for both before either SaveChanges landed; the partial
            // unique index `uq_project_team_slug_active` catches the loser at write
            // time. Translate to the same friendly error the pre-check would have
            // emitted so the operator sees one consistent "rename" message instead
            // of a raw 500.
            _db.Project.Local.Remove(project);
            throw new InvalidOperationException(
                $"A project with slug '{slug}' (derived from name '{name}') already exists in this team. " +
                $"Pick a different project name — the slug is used as the prefix for variable references " +
                $"({{{{project.{slug}.X}}}}) and must be unique per team.", ex);
        }

        _logger.LogInformation("Project created: team={TeamId} project={ProjectId} slug={Slug}", teamId, project.Id, slug);
        return project.Id;
    }

    /// <summary>
    /// True iff the exception is a Postgres unique-violation on the team-slug partial
    /// unique index. Other 23505s (a future column with its own unique index) shouldn't
    /// be translated — let those bubble as 500 so we notice + add specific handling.
    /// </summary>
    private static bool IsTeamSlugUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == "23505"
            && (pg.ConstraintName?.Contains("uq_project_team_slug_active", StringComparison.Ordinal) ?? false);

    public async Task MoveRepositoryAsync(Guid teamId, Guid repositoryId, Guid targetProjectId, Guid actorUserId, CancellationToken cancellationToken)
    {
        // Verify target project belongs to the team — prevents cross-team moves through a
        // stolen project id. The single-row WHERE proves both existence and ownership.
        var targetOk = await _db.Project.AsNoTracking()
            .AnyAsync(p => p.Id == targetProjectId && p.TeamId == teamId && p.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);
        if (!targetOk)
            throw new KeyNotFoundException($"Target project {targetProjectId} not found or not accessible.");

        // Load + verify repo. Soft-deleted repos aren't movable — operator should rebind
        // them through the normal bind flow if they want them back.
        var repository = await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {repositoryId} not found or not accessible.");

        // Phase 3.1 — Repository:Project is N:M via project_repository link table. "Move" is
        // expressed as: detach every currently-active link of this repo, attach a fresh link
        // to the target. This preserves the legacy "this repo now belongs to exactly the
        // target project" contract that the frontend's Move-to-Project flow expects, while
        // the underlying schema supports many-to-many for callers that want to express
        // membership in multiple projects via the link table directly.
        var existingLinks = await _db.ProjectRepository
            .Where(pr => pr.RepositoryId == repositoryId && pr.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent: already linked to exactly the target only → no-op.
        if (existingLinks.Count == 1 && existingLinks[0].ProjectId == targetProjectId) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var link in existingLinks)
        {
            if (link.ProjectId == targetProjectId) continue;   // keep an existing-target link untouched
            link.DeletedDate = now;
            link.LastModifiedDate = now;
            link.LastModifiedBy = actorUserId;
        }

        if (!existingLinks.Any(l => l.ProjectId == targetProjectId && l.DeletedDate == null))
            _db.ProjectRepository.Add(new ProjectRepository
            {
                ProjectId = targetProjectId,
                RepositoryId = repositoryId,
                TeamId = teamId,
                CreatedDate = now,
                CreatedBy = actorUserId,
                LastModifiedDate = now,
                LastModifiedBy = actorUserId,
            });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Repository moved: team={TeamId} repository={RepositoryId} detached={DetachedCount} to={ToProjectId}",
            teamId, repositoryId, existingLinks.Count(l => l.ProjectId != targetProjectId), targetProjectId);
    }

    /// <summary>
    /// Derives the slug from a project name. Lowercase + ASCII-safe + max 64 chars, matching
    /// the DB CHECK on <c>project.slug</c> (<c>^[A-Za-z0-9_-]{1,64}$</c>). Spaces and
    /// punctuation collapse to single hyphens; leading/trailing hyphens trim; consecutive
    /// hyphens collapse. Returns an empty string when no characters survive — the caller
    /// throws an actionable error in that case.
    /// <para>Examples:
    /// <list type="bullet">
    ///   <item><c>"Acme Backend"</c> → <c>"acme-backend"</c></item>
    ///   <item><c>"My Product 2024!"</c> → <c>"my-product-2024"</c></item>
    ///   <item><c>"---spaces---"</c> → <c>"spaces"</c></item>
    /// </list></para>
    /// </summary>
    public static string SlugifyName(string name) => Slug.Slugify(name);

    public async Task UpdateAsync(Guid teamId, Guid projectId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required", nameof(name));

        var project = await LoadActiveAsync(teamId, projectId, cancellationToken).ConfigureAwait(false);

        project.Name = name;
        project.Description = description;
        project.LastModifiedDate = DateTimeOffset.UtcNow;
        project.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Project updated: team={TeamId} project={ProjectId}", teamId, projectId);
    }

    public async Task DeleteAsync(Guid teamId, Guid projectId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var project = await LoadActiveAsync(teamId, projectId, cancellationToken).ConfigureAwait(false);

        if (string.Equals(project.Slug, DefaultProjectSlug, StringComparison.Ordinal))
            throw new InvalidOperationException("The default project cannot be deleted — every team must have at least one project.");

        var activeRepoCount = await _db.ProjectRepository.AsNoTracking()
            .CountAsync(pr => pr.ProjectId == projectId && pr.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (activeRepoCount > 0)
            throw new InvalidOperationException(
                $"Project {project.Slug} still has {activeRepoCount} active repositor{(activeRepoCount == 1 ? "y" : "ies")}. Move or unbind them first.");

        var now = DateTimeOffset.UtcNow;
        project.DeletedDate = now;
        project.LastModifiedDate = now;
        project.LastModifiedBy = actorUserId;

        // Cascade: soft-delete every project-scoped variable so {{project.{Slug}.X}} references
        // resolve to null going forward. We don't hard-delete — operator audit needs the trail.
        var variableRows = await _db.Variable
            .Where(v => v.Scope == VariableScope.Project && v.ScopeId == projectId && v.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var v in variableRows)
        {
            v.DeletedDate = now;
            v.LastModifiedDate = now;
            v.LastModifiedBy = actorUserId;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Project soft-deleted: team={TeamId} project={ProjectId} cascaded_variables={Count}",
            teamId, projectId, variableRows.Count);
    }

    public async Task<Guid> EnsureDefaultProjectAsync(Guid teamId, CancellationToken cancellationToken)
    {
        // Fast path — lookup an existing active default project.
        var existing = await _db.Project.AsNoTracking()
            .Where(p => p.TeamId == teamId && p.Slug == DefaultProjectSlug && p.DeletedDate == null)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing.HasValue) return existing.Value;

        // Slow path — race-safe create. The (team_id, slug) partial unique index
        // guarantees only one row wins under concurrency; the loser catches the unique
        // violation and re-reads. For simplicity we just retry with a single re-fetch.
        try
        {
            // EnsureDefault uses a direct INSERT path because we want slug=='default'
            // verbatim, not derived from a Name that might slugify differently.
            EnsureValidSlug(DefaultProjectSlug);

            var now = DateTimeOffset.UtcNow;
            var project = new Project
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                Slug = DefaultProjectSlug,
                Name = "Default",
                Description = "Default project for repositories and variables. Auto-created when this team was provisioned; rename or add additional projects as your team grows.",
                CreatedDate = now,
                CreatedBy = SystemUsers.SeederId,
                LastModifiedDate = now,
                LastModifiedBy = SystemUsers.SeederId,
            };
            _db.Project.Add(project);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Default project lazily created: team={TeamId} project={ProjectId}", teamId, project.Id);
            return project.Id;
        }
        catch (DbUpdateException)
        {
            // Someone else won the race. Re-read.
            return await _db.Project.AsNoTracking()
                .Where(p => p.TeamId == teamId && p.Slug == DefaultProjectSlug && p.DeletedDate == null)
                .Select(p => p.Id)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Project> LoadActiveAsync(Guid teamId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Project
            .Where(p => p.Id == projectId && p.TeamId == teamId && p.DeletedDate == null)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return project ?? throw new KeyNotFoundException($"Project {projectId} not found or not accessible.");
    }

    private async Task EnsureSlugAvailableAsync(Guid teamId, string slug, string requestedName, CancellationToken cancellationToken)
    {
        var exists = await _db.Project.AsNoTracking()
            .AnyAsync(p => p.TeamId == teamId && p.Slug == slug && p.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        // Message names BOTH the derived slug AND the requested name so the operator
        // understands what to change. We refuse to auto-mangle into "{slug}-2" because
        // the slug is part of the variable-path contract — a silent mangle would surprise
        // the operator when they later try to reference {{project.{X}.foo}} from a
        // workflow and the path they expected doesn't exist.
        if (exists)
            throw new InvalidOperationException(
                $"A project with slug '{slug}' (derived from name '{requestedName}') already exists in this team. " +
                $"Pick a different project name — the slug is used as the prefix for variable references " +
                $"({{{{project.{slug}.X}}}}) and must be unique per team.");
    }

    private static void EnsureValidSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Project slug is required", nameof(slug));
        if (!SlugRegex.IsMatch(slug))
            throw new ArgumentException(
                $"Project slug '{slug}' is invalid. Allowed characters: A-Z, a-z, 0-9, underscore, hyphen. Length 1–64.",
                nameof(slug));
    }
}
