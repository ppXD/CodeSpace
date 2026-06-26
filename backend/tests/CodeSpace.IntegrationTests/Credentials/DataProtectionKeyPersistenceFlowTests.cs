using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Proves the multi-pod BLOCKER fix end to end against real Postgres: with the Data Protection key-ring persisted to
/// the shared database under a stable application name (CodeSpaceDataProtection), a credential encrypted by ONE pod
/// is decryptable by ANOTHER pod sharing the same DB. That is exactly the API + N-worker-replica topology that the
/// framework default (a per-process / content-root-local key-ring) silently breaks. Two independent DI containers
/// over the one test database stand in for two pods.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DataProtectionKeyPersistenceFlowTests
{
    private readonly PostgresFixture _fixture;

    public DataProtectionKeyPersistenceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_credential_encrypted_on_one_pod_decrypts_on_another_pod_via_the_shared_key_ring()
    {
        const string secret = "sk-super-secret-model-key";

        using var podA = BuildPod(_fixture.ConnectionString);
        using var podB = BuildPod(_fixture.ConnectionString);

        // podA encrypts (generating + persisting a key to the shared table on first use)…
        var ciphertext = podA.GetRequiredService<IPayloadEncryptor>().Encrypt(secret);

        // …and a DIFFERENT pod, which never saw podA's in-memory key-ring, decrypts it by reading the shared DB key-ring.
        podB.GetRequiredService<IPayloadEncryptor>().Decrypt(ciphertext).ShouldBe(secret,
            "a credential encrypted on one pod MUST decrypt on another replica — the key-ring is shared via Postgres, not per-pod ephemeral");

        // And the key really landed in the shared table (not a node-local file the next pod can't see).
        using var scope = podA.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().DataProtectionKeys.AsNoTracking().AnyAsync())
            .ShouldBeTrue("encrypting generated a Data Protection key persisted to data_protection_keys (DbUp 0074)");
    }

    /// <summary>
    /// One independent pod: an AUTOFAC container (mirroring production — the IServiceCollection DP registrations are
    /// Populate'd into Autofac, and the DbContext is registered Autofac-native via <c>builder.Register</c> exactly as
    /// <c>CodeSpaceModule.RegisterPersistence</c> does — NOT via AddDbContext). This exercises the SAME cross-container
    /// resolution the Data Protection key store performs in production: it resolves <see cref="CodeSpaceDbContext"/>
    /// (Autofac-only) through the populated Autofac IServiceProvider.
    /// </summary>
    private static AutofacServiceProvider BuildPod(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddCodeSpaceDataProtection();
        services.AddSingleton<IPayloadEncryptor, DataProtectionPayloadEncryptor>();

        var builder = new ContainerBuilder();
        builder.Populate(services);

        builder.Register(_ => new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options)
            .As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
        builder.Register(c => new CodeSpaceDbContext(c.Resolve<DbContextOptions<CodeSpaceDbContext>>()))
            .AsSelf().InstancePerLifetimeScope();

        return new AutofacServiceProvider(builder.Build());
    }
}
