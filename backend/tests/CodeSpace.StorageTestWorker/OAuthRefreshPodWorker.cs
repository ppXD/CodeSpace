using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.StorageTestWorker.PreviousBuild;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSpace.StorageTestWorker;

/// <summary>Which build a pod runs: this one's refresher, writer and refresh lock, or the pinned copies of the build before it.</summary>
public enum PodBuild
{
    This,
    Previous
}

/// <summary>
/// One "pod": a worker PROCESS with its own static refresh semaphores, its own Npgsql pools and its own DbContexts, running
/// the production OAuth refresher — or, for <see cref="PodBuild.Previous"/>, the pinned copy of the build before it — over the
/// parent's Postgres and GitLab. Every caller (a run, a webhook) loads the credential row FIRST; then the pod says
/// <c>ready</c> and waits for the parent's <c>go</c>, so that every caller of every pod refreshes at once, in the window a
/// burst of deliveries or parallel runs produces. Each caller prints one <c>RESULT {json}</c> line.
///
/// <para>The parent and the pods must share a Data Protection key ring, which is why the stack is wired by hand here, on a
/// key directory the parent names, rather than taken from a container whose key ring is the machine's.</para>
/// </summary>
public static class OAuthRefreshPodWorker
{
    public const string Mode = "oauth-refresh-pod";

    /// <summary>Arguments after <see cref="Mode"/>: connection string, key directory, credential id, callers, build.</summary>
    public static async Task<int> RunAsync(string connectionString, string keysDirectory, Guid credentialId, int callers, PodBuild build)
    {
        var stack = PodStack.Build(connectionString, keysDirectory);
        var refresher = build == PodBuild.This ? stack.ThisBuildRefresher() : stack.PreviousBuildRefresher();
        var loaded = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => LoadAsync(stack, credentialId))).ConfigureAwait(false);

        Barrier("ready", "go");

        var results = await Task.WhenAll(loaded.Select((caller, index) => RefreshAsync(refresher, caller, index))).ConfigureAwait(false);

        foreach (var result in results) Console.WriteLine("RESULT " + JsonSerializer.Serialize(result));

        return 0;
    }

    private static async Task<(ProviderContext Context, OAuthPayload Payload)> LoadAsync(PodStack stack, Guid credentialId)
    {
        await using var db = stack.NewDb();
        var credential = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        var instance = await db.ProviderInstance.AsNoTracking().SingleAsync(i => i.Id == credential.ProviderInstanceId).ConfigureAwait(false);

        return (new ProviderContext(instance, credential), (OAuthPayload)stack.Resolver.Resolve(credential));
    }

    private static async Task<object> RefreshAsync(IOAuthTokenRefresher refresher, (ProviderContext Context, OAuthPayload Payload) caller, int index)
    {
        try
        {
            var effective = await refresher.RefreshIfNeededAsync(caller.Context, caller.Payload, CancellationToken.None).ConfigureAwait(false);

            return new { caller = index, ok = true, accessToken = effective.AccessToken, refreshToken = effective.RefreshToken };
        }
        catch (Exception ex)
        {
            return new { caller = index, ok = false, error = ex.GetType().Name, message = ex.Message };
        }
    }

    private static void Barrier(string reached, string awaited)
    {
        Console.WriteLine(reached);

        if (Console.ReadLine() != awaited) throw new IOException($"The parent closed the {reached} barrier.");
    }
}

/// <summary>The production classes of the OAuth refresh path wired by hand, as DI would, over a filesystem key ring shared by every pod and the parent.</summary>
public sealed class PodStack
{
    private readonly string _connectionString;
    private readonly CodeSpaceConnectionString _settings;
    private readonly IOAuthClientRegistry _clients;
    private readonly ICredentialPayloadReader _reader;

    private PodStack(string connectionString, string keysDirectory)
    {
        _connectionString = connectionString;
        _settings = new CodeSpaceConnectionString(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CodeSpaceStore:ConnectionString"] = connectionString }).Build());

        var keyRing = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory), options => options.SetApplicationName(CodeSpaceDataProtection.ApplicationName));
        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        Encryptor = new DataProtectionPayloadEncryptor(keyRing);
        Serializer = new CredentialPayloadSerializer();
        Resolver = new CredentialResolver(Encryptor, Serializer);
        _reader = new CredentialPayloadReader(_settings);
        _clients = new OAuthClientRegistry(new IOAuthClient[] { new GitLabOAuthClient(http, TimeProvider.System) });
    }

    public IPayloadEncryptor Encryptor { get; }

    public ICredentialPayloadSerializer Serializer { get; }

    public ICredentialResolver Resolver { get; }

    public static PodStack Build(string connectionString, string keysDirectory) => new(connectionString, keysDirectory);

    public IOAuthTokenRefresher ThisBuildRefresher()
    {
        var trackedEntry = new TrackedCredentialEntry(NewDb());

        return new OAuthTokenRefresher(_clients, new CredentialPayloadWriter(Encryptor, Serializer, _settings, trackedEntry), _reader, Resolver, Encryptor, TimeProvider.System, new PostgresAdvisoryLock(_settings), trackedEntry, new CredentialHealthWriter(_settings), NullLogger<OAuthTokenRefresher>.Instance);
    }

    public IOAuthTokenRefresher PreviousBuildRefresher() =>
        new PreviousBuildOAuthTokenRefresher(_clients, new PreviousBuildCredentialPayloadWriter(Encryptor, Serializer, _settings), _reader, Resolver, Encryptor, TimeProvider.System, new PreviousBuildAdvisoryLock(_settings), NullLogger<PreviousBuildOAuthTokenRefresher>.Instance);

    public CodeSpaceDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)).Options);

    /// <summary>What the credential row holds now, decrypted — what the next refresh would present.</summary>
    public async Task<OAuthPayload> ReadStoredAsync(Guid credentialId)
    {
        var sealedPayload = await _reader.ReadEncryptedPayloadAsync(credentialId, CancellationToken.None).ConfigureAwait(false);

        return (OAuthPayload)Serializer.Deserialize(AuthType.OAuth, Encryptor.Decrypt(sealedPayload));
    }
}
