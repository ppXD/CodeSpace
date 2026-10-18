using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Resolves a repository's access token the way a run does — a scope of its own, the repository loaded untracked with its
/// instance and credential, then the real auth resolver → OAuth strategy → refresher → Postgres advisory lock → payload
/// writer — optionally with the payload writer wrapped, to break the store the way a pool exhaustion or a dropped
/// connection would.
/// </summary>
public static class CredentialRefreshHarness
{
    public static async Task<string> ResolveTokenAsync(PostgresFixture fixture, Guid repositoryId, Func<ICredentialPayloadWriter, ICredentialPayloadWriter>? wrapWriter = null, CancellationToken cancellationToken = default)
    {
        using var scope = fixture.BeginScope();
        using var runScope = wrapWriter == null ? null : scope.BeginLifetimeScope(b => b.RegisterInstance(wrapWriter(scope.Resolve<ICredentialPayloadWriter>())).As<ICredentialPayloadWriter>());
        ILifetimeScope resolving = runScope ?? scope;

        var repo = await LoadUntrackedAsync(resolving, repositoryId).ConfigureAwait(false);
        var auth = await resolving.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), cancellationToken).ConfigureAwait(false);

        return auth.Token;
    }

    /// <summary>Loads the repository the way the workspace resolver does: untracked, with its instance and credential.</summary>
    public static async Task<Repository> LoadUntrackedAsync(ILifetimeScope scope, Guid repositoryId) =>
        await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().Include(r => r.ProviderInstance).Include(r => r.Credential).SingleAsync(r => r.Id == repositoryId).ConfigureAwait(false);
}

/// <summary>
/// A payload writer whose first <c>failures</c> stores throw, as an exhausted pool, a dropped connection or a failover
/// does after the provider has already rotated the pair; every later store goes through to the real writer.
/// </summary>
public sealed class FailingCredentialPayloadWriter : ICredentialPayloadWriter
{
    private readonly ICredentialPayloadWriter _inner;
    private int _failuresLeft;
    private int _attempts;

    public FailingCredentialPayloadWriter(ICredentialPayloadWriter inner, int failures)
    {
        _inner = inner;
        _failuresLeft = failures;
    }

    /// <summary>How many times the refresher tried to store, failed or not.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);

        if (Interlocked.Decrement(ref _failuresLeft) >= 0) throw new NpgsqlException("simulated: the connection pool has been exhausted / the connection terminated while storing the rotated pair");

        return _inner.UpdatePayloadAsync(credential, expectedEncryptedPayload, newPayload, cancellationToken);
    }
}
