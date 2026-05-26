using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Dtos.ProviderInstances;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.ProviderInstances;

namespace CodeSpace.Core.Services.ProviderInstances;

/// <summary>
/// CRUD + usage preview for provider-instance records (the per-team config that
/// identifies a Git host + its OAuth app credentials).
/// </summary>
public interface IProviderInstanceService
{
    Task<IReadOnlyList<ProviderInstanceSummary>> ListAsync(CancellationToken cancellationToken);
    Task<ProviderInstanceUsage> GetUsageAsync(Guid providerInstanceId, CancellationToken cancellationToken);
    Task<Guid> AddAsync(ProviderKind provider, string displayName, string baseUrl, string? apiUrl, string? webUrl, string? oauthClientId, string? oauthClientSecret, string? oauthRedirectPath, IReadOnlyList<string>? oauthDefaultScopes, CancellationToken cancellationToken);
    Task UpdateAsync(UpdateProviderInstanceCommand request, CancellationToken cancellationToken);
    Task<DeleteProviderInstanceResult> DeleteAsync(Guid providerInstanceId, bool force, CancellationToken cancellationToken);
}
