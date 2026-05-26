using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Outbox;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Settings;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Serilog;
using Serilog.Extensions.Logging;

namespace CodeSpace.IntegrationTests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly IConfiguration _testConfig;
    private readonly string _adminConnectionString;
    private readonly string _testDatabaseName;

    private Autofac.IContainer? _ioc;

    public PostgresFixture()
    {
        _testConfig = BuildTestConfiguration();

        _adminConnectionString = new TestPostgresAdminConnectionString(_testConfig).Value;

        var prefix = new TestDatabaseNamePrefix(_testConfig).Value;
        _testDatabaseName = $"{prefix}{Guid.NewGuid():N}";
    }

    public string ConnectionString { get; private set; } = default!;

    public ILifetimeScope BeginScope() => _ioc!.BeginLifetimeScope();

    /// <summary>
    /// Begins a child scope with a per-test override callback — typically used to swap
    /// <see cref="CodeSpace.Core.Services.Identity.ICurrentUser"/> for tenancy tests so the
    /// caller can simulate "user in team" vs "user not in team" cases.
    /// </summary>
    public ILifetimeScope BeginScope(Action<ContainerBuilder> configure) => _ioc!.BeginLifetimeScope(configure);

    /// <summary>
    /// Convenience: opens a scope with the given user + team + roles injected as
    /// ICurrentUser and ICurrentTeam. Pass null for either to omit (anonymous or no team).
    /// Pass Roles.Admin to bypass tenancy without seeding TeamMembership rows.
    /// </summary>
    public ILifetimeScope BeginScopeAs(Guid? userId, Guid? teamId, params string[] roles)
    {
        return BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", roles)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
        });
    }

    /// <summary>
    /// Drains every Pending outbox message synchronously. Tests that exercise the bind flow
    /// must call this after Send-ing a bind command — the bind only enqueues; the dispatcher
    /// is what actually registers the webhook and inserts the RepositoryWebhook row.
    /// </summary>
    public async Task DrainOutboxAsync(CancellationToken cancellationToken = default)
    {
        const int safetyCap = 100;
        var totalProcessed = 0;

        while (totalProcessed < safetyCap)
        {
            using var scope = BeginScope();
            var dispatcher = scope.Resolve<IOutboxDispatcher>();
            var processed = await dispatcher.DrainOnceAsync(50, cancellationToken).ConfigureAwait(false);

            if (processed == 0) return;

            totalProcessed += processed;
        }
    }

    public async Task InitializeAsync()
    {
        await CreateTestDatabaseAsync().ConfigureAwait(false);

        ConnectionString = BuildTestConnectionString();

        new DbUpRunner(ConnectionString).Run();

        // TeamSecretEncryption normally reads CODESPACE_TEAM_SECRET_MASTER_KEY at startup
        // and fail-fasts when missing in non-Development. The integration suite needs a
        // stable key so encrypted-at-rest assertions can decrypt across fixture rebuilds —
        // pin a deterministic 32-byte test key here. Production deploys set this env var
        // via secret-store; tests set it programmatically.
        const string testMasterKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        Environment.SetEnvironmentVariable("CODESPACE_TEAM_SECRET_MASTER_KEY", testMasterKeyBase64);

        _ioc = BuildIocContainer(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _ioc?.Dispose();
        await DropTestDatabaseAsync().ConfigureAwait(false);
    }

    private static IConfiguration BuildTestConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private async Task CreateTestDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_testDatabaseName}\"", conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task DropTestDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var killCmd = new NpgsqlCommand(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_testDatabaseName}' AND pid <> pg_backend_pid()",
            conn);
        await killCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await using var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_testDatabaseName}\"", conn);
        await dropCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string BuildTestConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _testDatabaseName };
        return builder.ConnectionString;
    }

    private Autofac.IContainer BuildIocContainer(string connectionString)
    {
        var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_testConfig)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeSpaceStore:ConnectionString"] = connectionString,
                ["Authentication:Jwt:SymmetricKey"] = "test-only-key-do-not-use-in-prod-minimum-32-chars",
                ["OAuth:CallbackUrl"] = "http://localhost:5099/api/credentials/oauth/callback"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(logger));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection();
        // HeaderCurrentTeam takes IHttpContextAccessor; tests have no real HttpContext so this
        // returns a singleton with null HttpContext, and HeaderCurrentTeam.Id becomes null —
        // tenancy behaviors then throw "X-Team-Id missing" unless the test overrides ICurrentTeam.
        services.AddHttpContextAccessor();
        // GitHubOAuthClient + GitLabOAuthClient take IHttpClientFactory; register it so the
        // singletons can be constructed even when tests don't hit the live HTTP path.
        services.AddHttpClient();

        var builder = new ContainerBuilder();
        builder.Populate(services);
        builder.RegisterModule(new CodeSpaceModule(logger, configuration));

        RegisterTestAssemblyTypes(builder);

        return builder.Build();
    }

    private static void RegisterTestAssemblyTypes(ContainerBuilder builder)
    {
        var testAssembly = typeof(PostgresFixture).Assembly;

        builder.RegisterAssemblyTypes(testAssembly)
            .AsClosedTypesOf(typeof(IRequestHandler<,>))
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(testAssembly)
            .AsClosedTypesOf(typeof(INotificationHandler<>))
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(testAssembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(CodeSpace.Core.Services.Providers.Capabilities.IProviderCapability).IsAssignableFrom(t))
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(testAssembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(CodeSpace.Core.Services.Providers.Events.IProviderEventSubscription).IsAssignableFrom(t))
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        // Test-only outbox handlers (NoOp) registered alongside production ones.
        builder.RegisterAssemblyTypes(testAssembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(CodeSpace.Core.Services.Outbox.IOutboxMessageHandler).IsAssignableFrom(t))
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        var settingTypes = testAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IConfigurationSetting).IsAssignableFrom(t))
            .ToArray();

        builder.RegisterTypes(settingTypes).AsSelf().SingleInstance();

        builder.RegisterType<Webhooks.CapturedNormalizedEvents>().AsSelf().SingleInstance();

        // IBackgroundJobClient test impl. Records Enqueue calls + lets tests simulate
        // Hangfire failure via ThrowOnEnqueue. SingleInstance so tests can assert the
        // recorded call list across fixture scopes.
        builder.RegisterType<Jobs.InMemoryBackgroundJobClient>()
            .As<CodeSpace.Core.Services.Jobs.IBackgroundJobClient>()
            .AsSelf()
            .SingleInstance();
    }
}
