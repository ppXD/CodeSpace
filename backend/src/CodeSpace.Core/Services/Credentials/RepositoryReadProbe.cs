using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed class RepositoryReadProbe : IRepositoryReadProbe, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<RepositoryReadProbe> _logger;

    public RepositoryReadProbe(CodeSpaceDbContext db, IProviderRegistry registry, ILogger<RepositoryReadProbe> logger)
    {
        _db = db;
        _registry = registry;
        _logger = logger;
    }

    public async Task<RepositoryRead> ReadAsync(Credential target, string externalId, string? sourceName, CancellationToken cancellationToken)
    {
        var instance = target.ProviderInstance ?? await _db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == target.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
        var catalog = _registry.Require<IRepositoryCatalogCapability>(instance.Provider);

        try
        {
            var remote = await catalog.GetByExternalIdAsync(new ProviderContext(instance, target), externalId, cancellationToken).ConfigureAwait(false);

            return new RepositoryRead(true, remote.FullPath);
        }
        catch (Exception ex) when (TargetIsRefused(ex))
        {
            _logger.LogWarning(ex, "Credential {CredentialId} could not read repository {ExternalId}; the move onto it is refused", target.Id, externalId);

            return RepositoryRead.Refused;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not check whether credential {CredentialId} reads repository {ExternalId}, because the provider did not answer; the move onto it is refused, to be tried again", target.Id, externalId);

            throw new RelinkVerificationUnavailableException(target.DisplayName, sourceName, ex);
        }
    }

    /// <summary>
    /// The provider answered, and the answer is about the target: this token is not let in (401, 403 or an insufficient
    /// scope), cannot see that project (404), or — for an OAuth connection — the grant itself was refused when its token was
    /// refreshed (the provider's answer, or the credential already marked as needing a reconnect for it). Anything else (a 5xx, a 429, a dropped connection, a local rate limit) says nothing about the target.
    /// </summary>
    private static bool TargetIsRefused(Exception ex) => ex is ProviderInsufficientScopeException or OAuthExchangeException or CredentialNeedsReconnectException or ProviderApiException { StatusCode: 401 or 403 or 404 };
}
