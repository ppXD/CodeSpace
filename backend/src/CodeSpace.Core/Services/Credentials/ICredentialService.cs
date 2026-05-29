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
    Task<CredentialUsage> GetUsageAsync(Guid credentialId, CancellationToken cancellationToken);
    Task<CredentialCapabilitiesResponse> GetCapabilitiesAsync(Guid credentialId, CancellationToken cancellationToken);
    Task<Guid> AddAsync(AddCredentialInput input, CancellationToken cancellationToken);
    Task<RevokeCredentialResult> RevokeAsync(Guid credentialId, CancellationToken cancellationToken);
    Task<RemoteRepositoryPage> ListAccessibleRepositoriesAsync(Guid credentialId, string? search, int page, int perPage, CancellationToken cancellationToken);
}
