using System.Security.Claims;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Services.Identity;
using Microsoft.AspNetCore.Http;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Composes the checks a request already passed at entry, rather than restating them, so the re-ask cannot drift from
/// the original: the token lifetime the bearer handler enforces (<see cref="BearerTokenLifetime"/>, the same skew), the
/// session verdict it reaches (<see cref="ISessionValidator"/>), and the membership answer the tenancy behaviors reach
/// (<see cref="TeamMembershipResolver"/>), global-Admin bypass included.
/// </summary>
public sealed class TeamStandingCheck : ITeamStandingCheck, IScopedDependency
{
    private readonly ICurrentUser _currentUser;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISessionValidator _sessionValidator;
    private readonly TeamMembershipResolver _resolver;
    private readonly TimeProvider _clock;

    public TeamStandingCheck(ICurrentUser currentUser, IHttpContextAccessor httpContextAccessor, ISessionValidator sessionValidator, TeamMembershipResolver resolver, TimeProvider clock)
    {
        _currentUser = currentUser;
        _httpContextAccessor = httpContextAccessor;
        _sessionValidator = sessionValidator;
        _resolver = resolver;
        _clock = clock;
    }

    public bool WithinTokenLifetime() => BearerTokenLifetime.Accepts(Principal(), _clock.GetUtcNow());

    public async Task<bool> HoldsAsync(Guid teamId, CancellationToken cancellationToken) =>
        WithinTokenLifetime() && await HoldsLiveSessionAsync(cancellationToken).ConfigureAwait(false) && await HoldsTeamStandingAsync(teamId, cancellationToken).ConfigureAwait(false);

    /// <summary>A password change or reset, or a deactivation, rotates the stamp, which ends every token minted before it.</summary>
    private async Task<bool> HoldsLiveSessionAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.Id is not { } userId) return false;

        var verdict = await _sessionValidator.VerifyAsync(userId, PresentedStamp(), cancellationToken).ConfigureAwait(false);

        return verdict == SessionVerdict.Live;
    }

    private async Task<bool> HoldsTeamStandingAsync(Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            await _resolver.EnsureMembershipAsync(teamId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TenantAccessDeniedException)
        {
            return false;
        }
    }

    private ClaimsPrincipal? Principal() => _httpContextAccessor.HttpContext?.User;

    private string? PresentedStamp() => Principal()?.FindFirst(SessionValidator.SecurityStampClaim)?.Value;
}
