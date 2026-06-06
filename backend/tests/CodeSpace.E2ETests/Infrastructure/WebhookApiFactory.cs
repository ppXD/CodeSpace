using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Hosts the REAL ASP.NET pipeline (routing, [AllowAnonymous], model binding, the controller's
/// try/catch, the GlobalExceptionFilter) in-memory via <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// against a throw-away GUID-named Postgres database. This is the E2E tier: the integration suite
/// hand-builds its own container and calls IMediator directly, so it never exercises the HTTP
/// surface — the actual status code a webhook provider receives lives here.
///
/// <para>Per-fixture lifecycle: create a GUID DB + DbUp on InitializeAsync, drop it on DisposeAsync.
/// The app's connection string is overridden to that DB; the Hangfire-backed job client is swapped
/// for a no-op so dispatch still writes the workflow_run row in-request but no engine executes.</para>
/// </summary>
public sealed class WebhookApiFactory : WebApplicationFactory<CodeSpace.Api.Program>, IAsyncLifetime
{
    private readonly string _adminConnectionString;
    private readonly string _testDatabaseName;
    private readonly string _testConnectionString;

    public WebhookApiFactory()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        _adminConnectionString = config.GetValue<string>("TestPostgres:AdminConnectionString")
            ?? throw new InvalidOperationException("TestPostgres:AdminConnectionString must be configured in appsettings.json");

        var prefix = config.GetValue<string>("TestPostgres:TestDatabaseNamePrefix") ?? "codespace_e2e_";
        _testDatabaseName = $"{prefix}{Guid.NewGuid():N}";
        _testConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _testDatabaseName }.ConnectionString;
    }

    public string ConnectionString => _testConnectionString;

    async Task IAsyncLifetime.InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_testDatabaseName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        new DbUpRunner(_testConnectionString).Run();

        // TeamSecretEncryption fail-fasts without this in non-Development; pin a deterministic key.
        Environment.SetEnvironmentVariable("CODESPACE_TEAM_SECRET_MASTER_KEY", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();

        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();

        await using (var kill = new NpgsqlCommand(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_testDatabaseName}' AND pid <> pg_backend_pid()", conn))
            await kill.ExecuteNonQueryAsync();

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_testDatabaseName}\"", conn);
        await drop.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development skips the HTTPS redirect (which would 307 the TestServer client); dev CORS +
        // OpenApi mapping are harmless for these tests.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeSpaceStore:ConnectionString"] = _testConnectionString,
                ["Authentication:Jwt:SymmetricKey"] = "test-only-key-do-not-use-in-prod-minimum-32-chars",
                ["OAuth:CallbackUrl"] = "http://localhost/api/credentials/oauth/callback",
            });
        });

    }

    /// <summary>
    /// The app wires Autofac on the generic host builder (<c>Program.CreateHostBuilder</c>), where
    /// <c>ConfigureTestContainer</c> isn't available. Appending a <c>ConfigureContainer</c> here — after
    /// the app's CodeSpaceModule — lets Autofac's last-registration-wins replace the Hangfire-backed
    /// job client with the no-op. Dispatch still writes the workflow_run row in the request transaction;
    /// we just don't fan out to a real Hangfire job / engine run.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureContainer<ContainerBuilder>(b =>
        {
            b.RegisterType<NoopBackgroundJobClient>().As<ICodeSpaceBackgroundJobClient>().InstancePerLifetimeScope();
        });

        return base.CreateHost(builder);
    }
}
