using System.Text.RegularExpressions;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Projects;

public sealed class ProjectService : IProjectService, IScopedDependency
{
    /// <summary>
    /// Mirror of the DB CHECK constraint. App-layer enforces the same rule so failures
    /// surface as a friendly 400 instead of a Postgres 23514. Single source of truth in
    /// terms of allowed character set — keep this regex in sync with migration 0022.
    /// </summary>
    public static readonly Regex SlugPattern = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(MapToSummary).ToList();
    }

    public async Task<ProjectSummary?> GetAsync(Guid projectId, Guid teamId, CancellationToken cancellationToken)
    {
        var row = await LoadActiveAsync(projectId, teamId, cancellationToken).ConfigureAwait(false);
        return row == null ? null : MapToSummary(row);
    }

    public async Task<ProjectSummary?> GetBySlugAsync(string slug, Guid teamId, CancellationToken cancellationToken)
    {
        var row = await _db.Project.AsNoTracking()
            .SingleOrDefaultAsync(p => p.TeamId == teamId && p.Slug == slug && p.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        return row == null ? null : MapToSummary(row);
    }

    public async Task<Guid> CreateAsync(Guid teamId, string slug, string name, string? description, Guid actorUserId, CancellationToken cancellationToken)
    {
        EnsureSlugValid(slug);
        await EnsureSlugFreeInTeamAsync(slug, teamId, cancellationToken).ConfigureAwait(false);

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

        _logger.LogInformation("Project created: id={ProjectId} team={TeamId} slug={Slug}", project.Id, teamId, slug);
        return project.Id;
    }

    public async Task UpdateAsync(Guid projectId, Guid teamId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken)
    {
        var row = await LoadActiveAsync(projectId, teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} not found or not accessible.");

        row.Name = name;
        row.Description = description;
        row.LastModifiedDate = DateTimeOffset.UtcNow;
        row.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Project updated: id={ProjectId} team={TeamId}", projectId, teamId);
    }

    public async Task DeleteAsync(Guid projectId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var row = await LoadActiveAsync(projectId, teamId, cancellationToken).ConfigureAwait(false);
        if (row == null) return;   // idempotent

        var now = DateTimeOffset.UtcNow;
        row.DeletedDate = now;
        row.LastModifiedDate = now;
        row.LastModifiedBy = actorUserId;

        // Cascade soft-delete the project's variables. ExecuteUpdateAsync is a single
        // statement → completes inside the outer EF transaction, no per-row tracking.
        await _db.Variable
            .Where(v => v.Scope == VariableScope.Project && v.ScopeId == projectId && v.DeletedDate == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.DeletedDate, (DateTimeOffset?)now)
                .SetProperty(v => v.LastModifiedDate, now)
                .SetProperty(v => v.LastModifiedBy, actorUserId), cancellationToken)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Project soft-deleted: id={ProjectId} team={TeamId}", projectId, teamId);
    }

    private async Task<Project?> LoadActiveAsync(Guid projectId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.Project
            .SingleOrDefaultAsync(p => p.Id == projectId && p.TeamId == teamId && p.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

    private async Task EnsureSlugFreeInTeamAsync(string slug, Guid teamId, CancellationToken cancellationToken)
    {
        var conflict = await _db.Project.AsNoTracking()
            .AnyAsync(p => p.TeamId == teamId && p.Slug == slug && p.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (conflict)
            throw new InvalidOperationException($"Project slug '{slug}' already exists in this team.");
    }

    private static void EnsureSlugValid(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) throw new InvalidOperationException("Project slug is required.");
        if (!SlugPattern.IsMatch(slug))
            throw new InvalidOperationException($"Project slug '{slug}' is invalid. Allowed: alphanumeric, underscore, hyphen; length 1-64; no dots or spaces.");
    }

    private static ProjectSummary MapToSummary(Project row) => new()
    {
        Id = row.Id,
        TeamId = row.TeamId,
        Slug = row.Slug,
        Name = row.Name,
        Description = row.Description,
        CreatedDate = row.CreatedDate,
        LastModifiedDate = row.LastModifiedDate,
    };
}
