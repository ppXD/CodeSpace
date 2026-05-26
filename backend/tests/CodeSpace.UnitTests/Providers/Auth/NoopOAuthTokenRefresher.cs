using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.UnitTests.Providers.Auth;

/// <summary>
/// Test substitute that returns the current payload unchanged. Use this when the test's
/// OAuthPayload has an ExpiresAt far in the future (no refresh expected) or null
/// (refresh impossible) — i.e. when the refresh path is not the subject of the test.
/// </summary>
internal sealed class NoopOAuthTokenRefresher : IOAuthTokenRefresher
{
    public Task<OAuthPayload> RefreshIfNeededAsync(ProviderContext context, OAuthPayload current, CancellationToken cancellationToken)
        => Task.FromResult(current);
}
