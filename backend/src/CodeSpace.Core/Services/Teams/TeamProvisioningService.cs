using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Messages.Dtos.Teams;
using CodeSpace.Messages.Enums;

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
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly TeamSlugAllocator _slugs;

    public TeamProvisioningService(CodeSpaceDbContext db, ICurrentUser currentUser, TeamSlugAllocator slugs)
    {
        _db = db;
        _currentUser = currentUser;
        _slugs = slugs;
    }

    public async Task<TeamSummary> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var ownerId = _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");
        var trimmed = name.Trim();

        if (trimmed.Length == 0) throw new ArgumentException("A team needs a name.", nameof(name));

        // Minted before the row is built because the slug falls back to it when the name — every
        // all-CJK one, for instance — cannot contribute anything a URL can carry.
        var teamId = Guid.NewGuid();
        var slug = await _slugs.ForWorkspaceAsync(trimmed, teamId, cancellationToken).ConfigureAwait(false);

        var team = Stage(new Team { Id = teamId, Name = trimmed, Slug = slug, Kind = TeamKind.Workspace }, ownerId);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new TeamSummary { Id = team.Id, Slug = team.Slug, Name = team.Name, Kind = team.Kind };
    }

    public async Task<Team> StagePersonalAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        var slug = await _slugs.ForPersonalAsync(ownerUserId, cancellationToken).ConfigureAwait(false);

        // PersonalForUserId is not ownership — the Owner row Stage writes is that. It is what the
        // partial unique index reads to hold the account to one active Personal team.
        return Stage(new Team { Id = Guid.NewGuid(), Name = "Personal", Slug = slug, Kind = TeamKind.Personal, PersonalForUserId = ownerUserId }, ownerUserId);
    }

    /// <summary>
    /// Everything it takes for a team to exist, staged as one unit of work: the team, the Owner
    /// membership row that IS its ownership, and the default project.
    ///
    /// <para>Staged rather than saved so both callers keep their own transaction shape — the
    /// invitation path is mid-way through creating an account when it asks for a personal team, and a
    /// save there would flush a half-built one.</para>
    /// </summary>
    private Team Stage(Team team, Guid ownerUserId)
    {
        _db.Team.Add(team);
        _db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = ownerUserId, Role = TeamRole.Owner });
        _db.Project.Add(ProjectService.BuildDefaultProject(team.Id, ownerUserId));

        return team;
    }
}
