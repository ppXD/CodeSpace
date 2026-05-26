using System.Text.RegularExpressions;
using CodeSpace.Core.DependencyInjection;
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
                ActiveRepositoryCount = _db.Repository.Count(r => r.ProjectId == p.Id && r.DeletedDate == null),
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

    public async Task<ProjectSummary?> GetAsync(Guid teamId, Guid projectId, CancellationToken cancellationToken)
    {
        var match = await _db.Project.AsNoTracking()
            .Where(p => p.Id == projectId && p.TeamId == teamId && p.DeletedDate == null)
            .Select(p => new
            {
                p.Id,
                p.TeamId,
                p.Slug,
                p.Name,
                p.Description,
                p.CreatedDate,
                ActiveRepositoryCount = _db.Repository.Count(r => r.ProjectId == p.Id && r.DeletedDate == null),
                ActiveVariableCount = _db.Variable.Count(v => v.Scope == VariableScope.Project && v.ScopeId == p.Id && v.DeletedDate == null),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (match == null) return null;

        return new ProjectSummary
        {
            Id = match.Id,
            TeamId = match.TeamId,
            Slug = match.Slug,
            Name = match.Name,
            Description = match.Description,
            CreatedDate = match.CreatedDate,
            ActiveRepositoryCount = match.ActiveRepositoryCount,
            ActiveVariableCount = match.ActiveVariableCount,
        };
    }

    public async Task<Guid> CreateAsync(Guid teamId, string slug, string name, string? description, Guid actorUserId, CancellationToken cancellationToken)
    {
        EnsureValidSlug(slug);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required", nameof(name));

        await EnsureSlugAvailableAsync(teamId, slug, cancellationToken).ConfigureAwait(false);

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
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Project created: team={TeamId} project={ProjectId} slug={Slug}", teamId, project.Id, slug);
        return project.Id;
    }

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

        var activeRepoCount = await _db.Repository.AsNoTracking()
            .CountAsync(r => r.ProjectId == projectId && r.DeletedDate == null, cancellationToken)
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
            return await CreateAsync(teamId, DefaultProjectSlug, "Default",
                "Default project for repositories and variables. Auto-created when this team was provisioned; rename or add additional projects as your team grows.",
                SystemUsers.SeederId, cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureSlugAvailableAsync(Guid teamId, string slug, CancellationToken cancellationToken)
    {
        var exists = await _db.Project.AsNoTracking()
            .AnyAsync(p => p.TeamId == teamId && p.Slug == slug && p.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
            throw new InvalidOperationException($"A project with slug '{slug}' already exists in this team.");
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
