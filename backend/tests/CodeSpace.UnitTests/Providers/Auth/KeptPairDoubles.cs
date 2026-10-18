using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.UnitTests.Providers.Auth;

/// <summary>
/// What the production resolver does with a credential its provider refused for good — fail with the typed
/// <c>credential_needs_reconnect</c> — and nothing else, in front of a payload the test chooses. With
/// <see cref="HoldingKeptPair"/> it is the pair of doubles that says whether a strategy stores the pair its process kept before
/// it reads the credential: the store lifts the verdict on the row, and only then is the credential readable.
/// </summary>
internal sealed class ErrorFailingCredentialResolver : ICredentialResolver
{
    private readonly CredentialPayload _payload;

    public ErrorFailingCredentialResolver(CredentialPayload payload) { _payload = payload; }

    public CredentialPayload Resolve(Credential credential) =>
        credential.Status == CredentialStatus.Error ? throw new CredentialNeedsReconnectException(credential.Id, credential.DisplayName, credential.LastError ?? "refused") : _payload;
}

/// <summary>A process that holds a rotated pair for the credential: asked to store it, it does, and the verdict a refusal elsewhere wrote on the row is lifted with it.</summary>
internal sealed class HoldingKeptPair : IOAuthTokenRefresher, IOAuthTokenRecovery
{
    public int Stores { get; private set; }

    public Task<OAuthPayload> RefreshIfNeededAsync(ProviderContext context, OAuthPayload current, CancellationToken cancellationToken) => Task.FromResult(current);

    public Task<OAuthPayload?> RecoverAfterRejectionAsync(ProviderContext context, string rejectedAccessToken, CancellationToken cancellationToken) => Task.FromResult<OAuthPayload?>(null);

    public Task StoreKeptPairAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        Stores++;

        context.Credential.Status = CredentialStatus.Active;
        context.Credential.LastError = null;

        return Task.CompletedTask;
    }
}
