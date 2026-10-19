using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
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
    private readonly ICredentialRevocationService _revocation;

    public TeamMemberService(CodeSpaceDbContext db, ICurrentUser currentUser, ICurrentTeam currentTeam, TeamMembershipResolver membership, ICredentialRevocationService revocation)
    {
        _db = db;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _membership = membership;
        _revocation = revocation;
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
    /// The same reach as every other act on a member, asked by things that act on a person without changing their
    /// membership — naming them as a credential's owner, say. They must have standing in this live team, and the caller
    /// must be them or outrank them. Someone without standing fails exactly like an id that names nobody. Held until the
    /// caller commits (<see cref="HoldMembershipAsync"/>), so what it writes in their name cannot land after they have gone.
    /// </summary>
    public async Task EnsureMayActOnAsync(Guid userId, CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();

        if (!await HoldMembershipAsync(teamId, userId, cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("that person is not a member of this team");

        var actor = await _membership.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);
        var target = await TeamStanding.RoleActedOnAsync(_db, teamId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("that person is not a member of this team");

        EnsureMayActOn(actor, target, isSelf: userId == RequireUser());
    }

    /// <summary>
    /// Whether they have standing (<see cref="TeamStanding"/>), held until the caller's transaction ends. A member's
    /// membership row is share-locked, after a share lock on the live team's row: it conflicts only with a departure's
    /// delete of that membership row (<see cref="RemoveMembershipAsync"/>) and the owner guard's lock on the team row
    /// (<see cref="LockTeamAsync"/>), so everything else in the team goes on beside it. Team row first, as an Owner's
    /// departure takes them: holding only the membership row, the write's own insert would wait on the team row that
    /// departure holds while the departure waits on the membership row.
    ///
    /// <para>An instance Admin outside the team has no row to hold, and none is held for the role: nothing in the app
    /// takes it away (<c>role_user</c> is written by migrations and by hand), so there is no departure to serialise with.
    /// A demotion that commits before this answers is refused here; one that commits after leaves a token the departure
    /// sweep retires on its next tick — the same window every token an ex-Admin lent already has between the demotion and
    /// that tick, and one in which nobody can act as them, because the act-as resolver asks standing at the write.</para>
    /// </summary>
    public async Task<bool> HoldMembershipAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await ShareLockLiveTeamAsync(teamId, cancellationToken).ConfigureAwait(false)) return false;

        await ShareLockMembershipAsync(teamId, userId, cancellationToken).ConfigureAwait(false);

        return await TeamStanding.HeldAsync(_db, teamId, userId, cancellationToken).ConfigureAwait(false);
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
        if (!TeamRoleRank.Reaches(actor, subject, isOwn: isSelf))
            throw new RoleOutranksActorException(actor, subject);
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

    /// <summary>
    /// Leaving and being removed end the same things, in the same transaction: the membership row, and every provider
    /// token the person lent the team. A personal token is that person's account — kept after they go, the team could
    /// still list it, browse what it reaches, bind more through it, and act as them. What is bound through it turns "needs
    /// a new credential" rather than silently keep a token nobody here may use.
    ///
    /// <para>The row's delete is saved before the retirement reads what goes, because what goes is decided by the
    /// departure sweep's own rule — no standing left in the team — and an instance Admin still stands in it without the
    /// row, so keeps what they lent. The mediator's transaction spans both saves.</para>
    ///
    /// <para>The delete is also what serialises a departure with every write in the person's name. It takes the row's
    /// exclusive lock, so it waits for each write holding that row (<see cref="HoldMembershipAsync"/>) to commit, and keeps
    /// any new one out until this departure commits — after which the row is gone and the write refuses. The retirement
    /// reads after the delete, so it sees a token such a write committed while the departure waited, and retires it with
    /// the rest instead of leaving it live for someone outside the team.</para>
    /// </summary>
    private async Task RemoveMembershipAsync(Guid teamId, TeamMembership membership, CancellationToken cancellationToken)
    {
        if (membership.Role == TeamRole.Owner) await EnsureAnotherOwnerRemainsAsync(teamId, membership.UserId, cancellationToken).ConfigureAwait(false);

        _db.TeamMembership.Remove(membership);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _revocation.RetireDepartedInTeamAsync(teamId, membership.UserId, cancellationToken).ConfigureAwait(false);

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

    private async Task<bool> ShareLockLiveTeamAsync(Guid teamId, CancellationToken cancellationToken) =>
        (await _db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM team WHERE id = {teamId} AND deleted_date IS NULL FOR KEY SHARE").ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

    private async Task ShareLockMembershipAsync(Guid teamId, Guid userId, CancellationToken cancellationToken) =>
        await _db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM team_membership WHERE team_id = {teamId} AND user_id = {userId} FOR KEY SHARE").ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task EnsureTeamLiveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var live = await _db.Team.AsNoTracking().AnyAsync(t => t.Id == teamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!live) throw new KeyNotFoundException($"team {teamId} not found");
    }

    private async Task<TeamMembership> LoadMembershipAsync(Guid teamId, Guid userId, CancellationToken cancellationToken) =>
        await _db.TeamMembership.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("that person is not a member of this team");

    private Guid RequireTeam() => _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

    private Guid RequireUser() => _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");
}
