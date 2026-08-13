using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Slugs;
using CodeSpace.Messages.Dtos.Teams;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Teams;

/// <summary>
/// Opening a new workspace.
///
/// <para>Whoever creates a team owns it, and that is recorded as an Owner membership row — the only
/// place ownership lives. A team created without one has no owner at all: it would show an empty
/// member list and refuse its own creator a role.</para>
/// </summary>
public sealed class TeamProvisioningService : ITeamProvisioningService, IScopedDependency
{
    /// <summary>
    /// Slugs that would collide with a URL the SPA already routes. "personal" is how migration 0008
    /// prefixes a personal workspace, and the frontend treats it as an alias.
    /// </summary>
    private static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal) { "personal", "admin", "new", "settings", "teams" };

    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;

    public TeamProvisioningService(CodeSpaceDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<TeamSummary> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var ownerId = _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");
        var trimmed = name.Trim();

        if (trimmed.Length == 0) throw new ArgumentException("A team needs a name.", nameof(name));

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            Slug = await DeriveSlugAsync(trimmed, cancellationToken).ConfigureAwait(false),
            Kind = TeamKind.Workspace,
        };

        _db.Team.Add(team);
        _db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = ownerId, Role = TeamRole.Owner });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new TeamSummary { Id = team.Id, Slug = team.Slug, Name = team.Name, Kind = team.Kind };
    }

    /// <summary>
    /// A readable slug from the name, deduplicated against what exists. Two teams called "Platform"
    /// is an ordinary thing to want, so the second becomes platform-2 rather than an error the person
    /// has to work around by inventing a different name.
    /// </summary>
    private async Task<string> DeriveSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slug.Slugify(name);

        if (baseSlug.Length == 0) baseSlug = "team";

        var taken = await _db.Team.AsNoTracking()
            .Where(t => t.DeletedDate == null && t.Slug.StartsWith(SlugDeduper.ProbePrefix(baseSlug)))
            .Select(t => t.Slug)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return SlugDeduper.DeriveAvailable(baseSlug, taken.ToHashSet(StringComparer.Ordinal), ReservedSlugs);
    }
}
