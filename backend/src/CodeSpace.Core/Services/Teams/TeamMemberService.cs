using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Teams;

/// <summary>
/// Changing who is in a team and what they may do.
///
/// <para>Two rules run through everything here.</para>
///
/// <para>Rank is a reach, not a threshold. You may act on anyone BELOW you and on yourself, never on
/// anyone above, and never across — two Admins who can demote each other turn a disagreement into a
/// race. Granting is the looser half: at your own rank is fine, which is what inviting an Admin as an
/// Admin already does; above it is a promotion you lack the standing to make.</para>
///
/// <para>And a team always has an owner. A team without one has nobody who could ever transfer
/// ownership, invite an owner, or delete it — unrecoverable rather than degraded — so every path out
/// of ownership checks that someone else still holds it.</para>
/// </summary>
public sealed class TeamMemberService : ITeamMemberService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;
    private readonly TeamMembershipResolver _membership;

    public TeamMemberService(CodeSpaceDbContext db, ICurrentUser currentUser, ICurrentTeam currentTeam, TeamMembershipResolver membership)
    {
        _db = db;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _membership = membership;
    }

    public async Task ChangeRoleAsync(Guid userId, TeamRole role, CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();
        var actor = await _membership.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);
        var target = await LoadMembershipAsync(teamId, userId, cancellationToken).ConfigureAwait(false);

        EnsureMayActOn(actor, target.Role, isSelf: userId == RequireUser());
        EnsureMayGrant(actor, role);

        if (target.Role == TeamRole.Owner && role != TeamRole.Owner) await EnsureAnotherOwnerRemainsAsync(teamId, userId, cancellationToken).ConfigureAwait(false);

        target.Role = role;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();
        var actor = await _membership.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);
        var target = await LoadMembershipAsync(teamId, userId, cancellationToken).ConfigureAwait(false);

        EnsureMayActOn(actor, target.Role, isSelf: userId == RequireUser());

        await RemoveMembershipAsync(teamId, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaving is not member management: it needs no permission because it changes only the caller's
    /// own row. The owner guard still applies — the last owner has to hand the team over before they
    /// can walk away from it.
    /// </summary>
    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();
        var userId = RequireUser();
        var membership = await LoadMembershipAsync(teamId, userId, cancellationToken).ConfigureAwait(false);

        await RemoveMembershipAsync(teamId, membership, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the team over in one step: the recipient becomes Owner and the outgoing owner becomes
    /// Admin.
    ///
    /// <para>Both together or neither — an owner who demoted themselves before the recipient was
    /// promoted would leave the team ownerless for the width of a failure.</para>
    /// </summary>
    public async Task TransferOwnershipAsync(Guid toUserId, CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();
        var actorId = RequireUser();

        if (toUserId == actorId) throw new ArgumentException("Ownership is already held by this account.", nameof(toUserId));

        await EnsureTeamLiveAsync(teamId, cancellationToken).ConfigureAwait(false);

        var recipient = await LoadMembershipAsync(teamId, toUserId, cancellationToken).ConfigureAwait(false);

        recipient.Role = TeamRole.Owner;

        await DemoteOutgoingOwnerAsync(teamId, actorId, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Steps the outgoing owner down to Admin — transferring is not leaving, so they stay in the team.
    ///
    /// <para>No row means the actor is not in this team, which only an instance Admin acting on
    /// somebody else's team can be. Writing them one would add them to a team they were never in, so
    /// there is nothing to demote and nothing to do.</para>
    /// </summary>
    private async Task DemoteOutgoingOwnerAsync(Guid teamId, Guid actorId, CancellationToken cancellationToken)
    {
        var outgoing = await _db.TeamMembership.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == actorId, cancellationToken).ConfigureAwait(false);

        if (outgoing == null) return;

        outgoing.Role = TeamRole.Admin;
    }

    // ── Guards ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// You may never reach above your own rank, and you may not reach ACROSS it either — two Admins
    /// who can demote each other turn a disagreement into a race. Yourself is the exception: stepping
    /// back from a role is not an act of authority over anyone, and the owner guard below is what
    /// stops it stranding the team.
    /// </summary>
    private static void EnsureMayActOn(TeamRole actor, TeamRole subject, bool isSelf)
    {
        if (TeamRoleRank.Of(subject) > TeamRoleRank.Of(actor)) throw new RoleOutranksActorException(actor, subject);

        if (TeamRoleRank.Of(subject) == TeamRoleRank.Of(actor) && !isSelf) throw new RoleOutranksActorException(actor, subject);
    }

    /// <summary>
    /// Granting at your own rank is allowed — an Admin may make another Admin, which is exactly what
    /// inviting at your own rank already does. Only granting ABOVE yourself is a promotion you do not
    /// have the standing to make.
    /// </summary>
    private static void EnsureMayGrant(TeamRole actor, TeamRole role)
    {
        if (TeamRoleRank.Of(role) > TeamRoleRank.Of(actor)) throw new RoleOutranksActorException(actor, role);
    }

    private async Task RemoveMembershipAsync(Guid teamId, TeamMembership membership, CancellationToken cancellationToken)
    {
        if (membership.Role == TeamRole.Owner) await EnsureAnotherOwnerRemainsAsync(teamId, membership.UserId, cancellationToken).ConfigureAwait(false);

        _db.TeamMembership.Remove(membership);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Owner ROWS, and only Owner rows. It used to fall back to <c>team.owner_user_id</c> naming
    /// someone else, which made the guard pass on a column the departing person's own leave had
    /// already failed to clear — the team read as owned by whoever the column still named.
    ///
    /// <para>Behind a lock on the team row, because counting and then deleting are two statements and
    /// read committed shows each of two departing Owners the other one still there. Both would pass a
    /// bare count and the team would end up with none. The column used to be a second record that made
    /// that recoverable; it is gone, so this is the only thing standing between a team and nobody
    /// being able to administer it — a state no one inside the team can undo.</para>
    ///
    /// <para>The team row rather than the membership rows: it is the one row every departure from this
    /// team contends on, whoever is leaving, so it serialises them against each other and against
    /// nothing else.</para>
    /// </summary>
    private async Task EnsureAnotherOwnerRemainsAsync(Guid teamId, Guid leavingUserId, CancellationToken cancellationToken)
    {
        await LockTeamAsync(teamId, cancellationToken).ConfigureAwait(false);

        var otherOwners = await _db.TeamMembership.AsNoTracking()
            .CountAsync(m => m.TeamId == teamId && m.UserId != leavingUserId && m.Role == TeamRole.Owner, cancellationToken)
            .ConfigureAwait(false);

        if (otherOwners == 0) throw new LastOwnerException();
    }

    /// <summary>
    /// Holds the team row until the surrounding transaction ends. Every write in this service runs
    /// inside one — the mediator opens it — so the lock spans the count and the delete that follows it.
    /// </summary>
    private async Task LockTeamAsync(Guid teamId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM team WHERE id = {teamId} FOR UPDATE", cancellationToken).ConfigureAwait(false);

    private async Task EnsureTeamLiveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var live = await _db.Team.AsNoTracking().AnyAsync(t => t.Id == teamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!live) throw new KeyNotFoundException($"team {teamId} not found");
    }

    private async Task<TeamMembership> LoadMembershipAsync(Guid teamId, Guid userId, CancellationToken cancellationToken) =>
        await _db.TeamMembership.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("that person is not a member of this team");

    private Guid RequireTeam() => _currentTeam.Id ?? throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"{HeaderCurrentTeam.HeaderName} header missing");

    private Guid RequireUser() => _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");
}
