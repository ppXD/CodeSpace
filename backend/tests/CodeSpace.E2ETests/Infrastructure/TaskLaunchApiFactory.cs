using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Hosts the REAL ASP.NET pipeline (routing, JWT auth, the X-Team-Id team-scope behavior, model binding, the
/// controller, the GlobalExceptionFilter, the mediator) in-memory against a throw-away GUID-named Postgres DB —
/// for the <c>POST /api/tasks</c> launch E2E. Unlike <see cref="WebhookApiFactory"/> (which swaps in a no-op job
/// client), this wires the <see cref="DeferredJobClient"/> as a SINGLETON so the launch endpoint's post-commit
/// dispatch → engine run → agent.code → executor → fake CLI → resume → terminal chain can be DRAINED after the
/// HTTP request returns — the run actually executes, end to end, behind the real HTTP surface.
/// </summary>
public sealed class TaskLaunchApiFactory : WebApplicationFactory<CodeSpace.Api.Program>, IAsyncLifetime
{
    private readonly string _adminConnectionString;
    private readonly string _testDatabaseName;
    private readonly string _testConnectionString;

    public TaskLaunchApiFactory()
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

    /// <summary>The JWT symmetric key the host signs with — the test mints a bearer token with the same key.</summary>
    public const string JwtKey = "test-only-key-do-not-use-in-prod-minimum-32-chars";

    async Task IAsyncLifetime.InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_testDatabaseName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        new DbUpRunner(_testConnectionString).Run();

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
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeSpaceStore:ConnectionString"] = _testConnectionString,
                ["Authentication:Jwt:SymmetricKey"] = JwtKey,
                ["OAuth:CallbackUrl"] = "http://localhost/api/credentials/oauth/callback",
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureContainer<ContainerBuilder>(b =>
        {
            // Singleton so deferred jobs survive past the request scope — the test drains them after the POST.
            b.RegisterType<DeferredJobClient>().As<ICodeSpaceBackgroundJobClient>().AsSelf().SingleInstance();
        });

        return base.CreateHost(builder);
    }

    /// <summary>The shared deferred-job client — the test drains it after the launch POST to run the engine + agent chain.</summary>
    public DeferredJobClient JobClient => Services.GetAutofacRoot().Resolve<DeferredJobClient>();
}
