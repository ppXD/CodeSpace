using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Credentials;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Credential lifecycle + read access. Excludes the OAuth flow itself (that's
/// IOAuthFlowService) — this surface is the local DB + provider catalog calls
/// that work for any auth type.
/// </summary>
public interface ICredentialService
{
    Task<IReadOnlyList<CredentialSummary>> ListAsync(Guid? providerInstanceId, CancellationToken cancellationToken);

    /// <summary>What a Disconnect of this credential, asked by the caller right now, would do: how many working repositories it would break, or how many it would carry to which connection instead.</summary>
    Task<CredentialUsage> GetUsageAsync(Guid credentialId, CancellationToken cancellationToken);

    Task<CredentialCapabilitiesResponse> GetCapabilitiesAsync(Guid credentialId, CancellationToken cancellationToken);
    Task<Guid> AddAsync(AddCredentialInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Revoke the credential and break (or move) what leans on it. <paramref name="moveBindingsToCredentialId"/> names the
    /// credential its repositories and group hooks follow in the same unit of work; left null, they follow the owner's
    /// only other live connection when the owner is the one disconnecting a connection that is still live and there is
    /// exactly one, and otherwise turn Error — see
    /// <see cref="RevokeCredentialCommand.MoveBindingsToCredentialId"/>. A target that may not take them is refused before
    /// the provider is asked to revoke anything.
    /// </summary>
    Task<RevokeCredentialResult> RevokeAsync(Guid credentialId, Guid? moveBindingsToCredentialId, CancellationToken cancellationToken);

    /// <summary>
    /// Put a new token into the credential, in place: it keeps its id, so everything bound through it keeps working and what
    /// its dead token had broken comes back. The token is probed first and must be the same provider account's
    /// (<see cref="ICredentialReauthorizer"/>). Any credential this caller manages, whatever it was before: a token replaces an
    /// OAuth sign-in as readily as another token, and the row's auth type follows. Tracked changes beyond the row's own write —
    /// the caller's unit of work saves them.
    /// </summary>
    Task<ReplaceCredentialTokenResult> ReplaceTokenAsync(Guid credentialId, string token, CancellationToken cancellationToken);

    Task<RemoteRepositoryPage> ListAccessibleRepositoriesAsync(Guid credentialId, string? search, int page, int perPage, CancellationToken cancellationToken);
}
