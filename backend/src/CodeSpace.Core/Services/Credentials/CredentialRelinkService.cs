using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The relink-all pipeline: check who is asking, load the dead credential and its replacement, refuse every target the move
/// would not accept, ask the target to read each repository it is about to take, then move. Every step either enriches
/// <c>_ctx</c> or throws; nothing is written until the last line, so a refusal leaves the team exactly as it was.
/// Dependents and suggestions are the same service's read side — see the partials.
/// </summary>
public sealed partial class CredentialRelinkService : ICredentialRelinkService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _membership;
    private readonly ICredentialSuccessionService _succession;
    private readonly IRepositoryReadProbe _probe;
    private readonly IActorIdentityResolver _actors;
    private readonly TimeProvider _clock;
    private readonly ILogger<CredentialRelinkService> _logger;

    private RelinkContext _ctx = new();

    public CredentialRelinkService(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver membership, ICredentialSuccessionService succession, IRepositoryReadProbe probe, IActorIdentityResolver actors, TimeProvider clock, ILogger<CredentialRelinkService> logger)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _membership = membership;
        _succession = succession;
        _probe = probe;
        _actors = actors;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RelinkCredentialBindingsResult> RelinkAllAsync(Guid fromCredentialId, Guid toCredentialId, CancellationToken cancellationToken)
    {
        _ctx = new RelinkContext();

        await EnsureMayManageCredentialsAsync(cancellationToken).ConfigureAwait(false);
        await LoadSourceAsync(fromCredentialId, cancellationToken).ConfigureAwait(false);
        EnsureSourceIsDead();

        await LoadTargetAsync(toCredentialId, cancellationToken).ConfigureAwait(false);
        EnsureTargetIsTheCallersOrTheTeams();
        await EnsureTargetMayTakeOverAsync(cancellationToken).ConfigureAwait(false);

        await LoadBoundRepositoriesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTargetReadsEveryRepositoryAsync(cancellationToken).ConfigureAwait(false);

        var moved = await _succession.MoveBindingsAsync(_ctx.From, _ctx.To, cancellationToken).ConfigureAwait(false);

        return BuildResult(moved.RepositoryCount, moved.ConnectionWebhookCount);
    }

    /// <summary>
    /// The command's marker holds the <see cref="TeamPermissions.ReposManage"/> half. This is the other half, asserted here
    /// because the credential marker that would carry it refuses the soft-deleted credential this runs for.
    /// </summary>
    private async Task EnsureMayManageCredentialsAsync(CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        var role = await _membership.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);

        if (!TeamPermissionMatrix.IsGranted(role, TeamPermissions.CredentialsManage))
            throw TenantAccessDeniedException.MissingTeamPermission(_currentUser.Id, teamId, role, TeamPermissions.CredentialsManage);
    }

    private async Task LoadSourceAsync(Guid id, CancellationToken cancellationToken) =>
        _ctx.From = await LoadTeamCredentialAsync(id, cancellationToken).ConfigureAwait(false);

    private async Task LoadTargetAsync(Guid id, CancellationToken cancellationToken) =>
        _ctx.To = await LoadTeamCredentialAsync(id, cancellationToken).ConfigureAwait(false);

    /// <summary>Moving bindings off a live credential is a Disconnect that says where they go — otherwise this could quietly change which grant a working repository acts through.</summary>
    private void EnsureSourceIsDead()
    {
        var from = _ctx.From;

        if (RelinkSuggestionRanking.IsLive(from))
            throw new InvalidOperationException($"Credential '{from.DisplayName}' is still connected. Disconnect it, choosing where its repositories go, rather than relinking from a live credential.");
    }

    /// <summary>Repositories bound through someone's personal connection act as that person; so the target is the caller's own, or the team's.</summary>
    private void EnsureTargetIsTheCallersOrTheTeams()
    {
        if (_currentUser.Id is Guid callerId && RelinkSuggestionRanking.IsCallersOrTeams(_ctx.To, callerId)) return;

        throw new InvalidOperationException($"Credential '{_ctx.To.DisplayName}' is another person's personal connection, and repositories bound through it would act as them. Pick one of your own connections, or a team token.");
    }

    private async Task EnsureTargetMayTakeOverAsync(CancellationToken cancellationToken)
    {
        if (await _succession.RefusalAsync(_ctx.From, _ctx.To, cancellationToken).ConfigureAwait(false) is { } refusal)
            throw new InvalidOperationException(refusal);
    }

    private RelinkCredentialBindingsResult BuildResult(int repositories, int connectionWebhooks) => new()
    {
        FromCredentialId = _ctx.From.Id,
        ToCredentialId = _ctx.To.Id,
        MovedRepositoryCount = repositories,
        MovedConnectionWebhookCount = connectionWebhooks
    };

    /// <summary>The team's credential by id, soft-deleted or not. One answer — not found — for a missing id and one from another team.</summary>
    private async Task<Credential> LoadTeamCredentialAsync(Guid id, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        return await _db.Credential.AsNoTracking().Include(c => c.ProviderInstance).SingleOrDefaultAsync(c => c.Id == id && c.TeamId == teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"credential {id} not found");
    }
}
