using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Users;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Invitations;

/// <summary>
/// Invitations: the only path by which an account comes into existence, since there is no public
/// sign-up.
///
/// <para>The link is the whole credential, so the plaintext token exists exactly once — in the reply
/// to the member who created it — and only its SHA-256 is stored. Every lookup is by hash, which is
/// also why the hash column is uniquely indexed: a collision would be an authorization bug.</para>
/// </summary>
public sealed class TeamInvitationService : ITeamInvitationService, IScopedDependency
{
    /// <summary>Long enough to survive a weekend, short enough that a link found later is dead.</summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;
    private readonly TeamMembershipResolver _membership;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenIssuer _tokenIssuer;
    private readonly IUserService _users;
    private readonly PublicBaseUrlSetting _baseUrl;
    private readonly TimeProvider _clock;

    public TeamInvitationService(CodeSpaceDbContext db, ICurrentUser currentUser, ICurrentTeam currentTeam, TeamMembershipResolver membership, IPasswordHasher hasher, IJwtTokenIssuer tokenIssuer, IUserService users, PublicBaseUrlSetting baseUrl, TimeProvider clock)
    {
        _db = db;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _membership = membership;
        _hasher = hasher;
        _tokenIssuer = tokenIssuer;
        _users = users;
        _baseUrl = baseUrl;
        _clock = clock;
    }

    public async Task<CreateInvitationResult> InviteAsync(string email, TeamRole role, CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();
        var normalized = Normalize(email);

        var team = await LoadInvitableTeamAsync(teamId, cancellationToken).ConfigureAwait(false);

        await EnsureGranterOutranksAsync(teamId, role, cancellationToken).ConfigureAwait(false);
        await EnsureNotAlreadyMemberAsync(team, normalized, cancellationToken).ConfigureAwait(false);
        await EnsureNoPendingInvitationAsync(teamId, normalized, cancellationToken).ConfigureAwait(false);

        var token = MintToken();
        var invitation = new TeamInvitation
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Email = normalized,
            Role = role,
            TokenHash = HashToken(token),
            Status = InvitationStatus.Pending,
            ExpiresAt = _clock.GetUtcNow() + InvitationLifetime,
            InvitedByUserId = RequireUser(),
        };

        _db.TeamInvitation.Add(invitation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateInvitationResult { InvitationId = invitation.Id, InviteUrl = _baseUrl.InviteUrl(token), ExpiresAt = invitation.ExpiresAt };
    }

    public async Task<IReadOnlyList<TeamInvitationSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();
        var now = _clock.GetUtcNow();

        return await _db.TeamInvitation.AsNoTracking()
            .Where(i => i.TeamId == teamId && i.Status == InvitationStatus.Pending)
            .OrderBy(i => i.Email)
            .Select(i => new TeamInvitationSummary
            {
                Id = i.Id,
                Email = i.Email,
                Role = i.Role,
                InvitedByName = i.InvitedBy.Name,
                ExpiresAt = i.ExpiresAt,
                IsExpired = i.ExpiresAt <= now,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await LoadPendingForTeamAsync(invitationId, cancellationToken).ConfigureAwait(false);

        invitation.Status = InvitationStatus.Revoked;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the token in place. The old link stops working immediately, which is the point —
    /// regenerate is what a member reaches for when they suspect the first link went astray.
    /// </summary>
    public async Task<CreateInvitationResult> RegenerateAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await LoadPendingForTeamAsync(invitationId, cancellationToken).ConfigureAwait(false);

        var token = MintToken();
        invitation.TokenHash = HashToken(token);
        invitation.ExpiresAt = _clock.GetUtcNow() + InvitationLifetime;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateInvitationResult { InvitationId = invitation.Id, InviteUrl = _baseUrl.InviteUrl(token), ExpiresAt = invitation.ExpiresAt };
    }

    public async Task<InvitationPreview> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        var invitation = await LoadUsableAsync(token, cancellationToken).ConfigureAwait(false);

        var team = await _db.Team.AsNoTracking().SingleAsync(t => t.Id == invitation.TeamId, cancellationToken).ConfigureAwait(false);
        var invitedBy = await _db.User.AsNoTracking().SingleAsync(u => u.Id == invitation.InvitedByUserId, cancellationToken).ConfigureAwait(false);

        return new InvitationPreview
        {
            TeamName = team.Name,
            InvitedByName = invitedBy.Name,
            Role = invitation.Role,
            Email = invitation.Email,
            ExpiresAt = invitation.ExpiresAt,
            AccountExists = await FindUserByEmailAsync(invitation.Email, cancellationToken).ConfigureAwait(false) != null,
        };
    }

    /// <summary>
    /// Spends the invitation and signs the invitee in.
    ///
    /// <para>One transaction for the whole thing, because a half-accepted invitation is worse than a
    /// failed one: an account with no personal workspace, or a spent token with no membership, both
    /// need a human to unpick. The <c>TransactionalBehavior</c> around the command supplies it.</para>
    /// </summary>
    public async Task<SignInResponse> AcceptAsync(string token, string? name, string? password, CancellationToken cancellationToken)
    {
        var invitation = await LoadUsableAsync(token, cancellationToken).ConfigureAwait(false);

        var user = await ResolveAccepterAsync(invitation, name, password, cancellationToken).ConfigureAwait(false);

        await EnsureNotAlreadyMemberByIdAsync(invitation.TeamId, user.Id, cancellationToken).ConfigureAwait(false);

        _db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = invitation.TeamId, UserId = user.Id, Role = invitation.Role });

        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedByUserId = user.Id;
        invitation.AcceptedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var issued = _tokenIssuer.Issue(user);

        return new SignInResponse { Token = issued.Token, ExpiresAt = issued.ExpiresAt, User = await _users.BuildMeForAsync(user, cancellationToken).ConfigureAwait(false) };
    }

    // ── Accepter ───────────────────────────────────────────────────────────────────

    private async Task<User> ResolveAccepterAsync(TeamInvitation invitation, string? name, string? password, CancellationToken cancellationToken)
    {
        var existing = await FindUserByEmailAsync(invitation.Email, cancellationToken).ConfigureAwait(false);

        if (existing == null) return await CreateAccepterAsync(invitation.Email, name, password, cancellationToken).ConfigureAwait(false);

        // The address has an account, so the account must prove itself. Without this, anyone holding
        // the link could set a password on someone else's account by "accepting" as them.
        var signedInId = _currentUser.Id ?? throw new InvitationRequiresSignInException();

        if (signedInId != existing.Id) throw new InvitationEmailMismatchException();

        return existing;
    }

    private async Task<User> CreateAccepterAsync(string email, string? name, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A name is required to create an account.", nameof(name));
        if (string.IsNullOrEmpty(password) || password.Length < UserService.MinPasswordLength) throw new ArgumentException($"Password must be at least {UserService.MinPasswordLength} characters.", nameof(password));

        var user = new User { Id = Guid.NewGuid(), Email = email, Name = name.Trim(), PasswordHash = _hasher.Hash(password) };

        _db.User.Add(user);
        AddPersonalTeam(user);
        await GrantDefaultPermissionsAsync(user, cancellationToken).ConfigureAwait(false);

        return user;
    }

    /// <summary>
    /// The grants every account holds from the moment it exists — today, just the right to open a
    /// workspace of their own.
    ///
    /// <para>Written here for the same reason the personal team is: this is the one path that creates
    /// an account, so this is where "every account has X" has to become true. Migration 0117 is the
    /// same statement for the accounts that already existed.</para>
    /// </summary>
    private async Task GrantDefaultPermissionsAsync(User user, CancellationToken cancellationToken)
    {
        var ids = await _db.Permission.AsNoTracking()
            .Where(p => Permissions.GrantedToEveryAccount.Contains(p.Name))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var permissionId in ids)
            _db.UserPermission.Add(new UserPermission { Id = Guid.NewGuid(), UserId = user.Id, PermissionId = permissionId });
    }

    /// <summary>
    /// Every account gets its own workspace at the moment it is created.
    ///
    /// <para>Migration 0008 backfilled one for every account that existed and holds "one active
    /// personal team per user" as a partial unique index — but nothing created one for a NEW account,
    /// because until now no path created accounts. This is that path, so this is where the invariant
    /// has to hold. The slug matches 0008's so the two are indistinguishable afterwards.</para>
    /// </summary>
    private void AddPersonalTeam(User user)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Slug = $"personal-{user.Id.ToString("N")[..8]}",
            Name = "Personal",
            Kind = TeamKind.Personal,
            OwnerUserId = user.Id,
        };

        _db.Team.Add(team);
        _db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Owner });
    }

    // ── Guards ─────────────────────────────────────────────────────────────────────

    private async Task<Team> LoadInvitableTeamAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var team = await _db.Team.AsNoTracking().SingleOrDefaultAsync(t => t.Id == teamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"team {teamId} not found");

        if (team.Kind == TeamKind.Personal) throw new PersonalTeamNotInvitableException();

        return team;
    }

    /// <summary>
    /// Nobody grants above their own standing. Without this an Admin invites an Owner and is one
    /// acceptance away from being overruled in their own team.
    /// </summary>
    private async Task EnsureGranterOutranksAsync(Guid teamId, TeamRole role, CancellationToken cancellationToken)
    {
        var granter = await _membership.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);

        if (TeamRoleRank.Of(role) > TeamRoleRank.Of(granter)) throw new InvitationRoleExceedsGranterException(role, granter);
    }

    private async Task EnsureNotAlreadyMemberAsync(Team team, string email, CancellationToken cancellationToken)
    {
        var existing = await FindUserByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        if (existing == null) return;

        await EnsureNotAlreadyMemberByIdAsync(team.Id, existing.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureNotAlreadyMemberByIdAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var isMember = await _db.Team.AsNoTracking()
            .AnyAsync(t => t.Id == teamId && (t.OwnerUserId == userId || t.Memberships.Any(m => m.UserId == userId)), cancellationToken)
            .ConfigureAwait(false);

        if (isMember) throw new AlreadyTeamMemberException();
    }

    private async Task EnsureNoPendingInvitationAsync(Guid teamId, string email, CancellationToken cancellationToken)
    {
        var pending = await _db.TeamInvitation.AsNoTracking()
            .AnyAsync(i => i.TeamId == teamId && i.Status == InvitationStatus.Pending && i.Email == email, cancellationToken)
            .ConfigureAwait(false);

        if (pending) throw new InvitationAlreadyPendingException(email);
    }

    // ── Lookup ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Anonymous: the token IS the authorization, so this is reachable without a session and must not
    /// distinguish its refusals. Expiry is evaluated here rather than swept, so a link dies on time
    /// without a job having to run.
    /// </summary>
    private async Task<TeamInvitation> LoadUsableAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);

        var invitation = await _db.TeamInvitation.SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken).ConfigureAwait(false);

        if (invitation == null || invitation.Status != InvitationStatus.Pending || invitation.ExpiresAt <= _clock.GetUtcNow()) throw new InvitationNotUsableException();

        return invitation;
    }

    private async Task<TeamInvitation> LoadPendingForTeamAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();

        return await _db.TeamInvitation.SingleOrDefaultAsync(i => i.Id == invitationId && i.TeamId == teamId && i.Status == InvitationStatus.Pending, cancellationToken).ConfigureAwait(false)
            ?? throw new InvitationNotUsableException();
    }

    private async Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken) =>
        await _db.User.SingleOrDefaultAsync(u => u.Email.ToLower() == email && u.DeletedDate == null, cancellationToken).ConfigureAwait(false);

    // ── Token ──────────────────────────────────────────────────────────────────────

    /// <summary>256 bits from the CSPRNG, base64url so it survives a URL unaltered.</summary>
    private static string MintToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Plain SHA-256, deliberately not a password hash: the token is 256 bits of entropy, so there is
    /// nothing to brute-force and a slow hash would only make every lookup slow. What matters is that
    /// a database dump does not contain working invitations.
    /// </summary>
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private Guid RequireTeam() => _currentTeam.Id ?? throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"{HeaderCurrentTeam.HeaderName} header missing");

    private Guid RequireUser() => _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");
}
