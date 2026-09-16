using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Serilog;
using Serilog.Core;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Hosts the app in the PROCESSING role — <c>HangfireHosting=Worker</c> — against a throw-away GUID-named Postgres
/// database. It is the only fixture in the suite that does: <see cref="TaskLaunchApiFactory"/> and
/// <see cref="WebhookApiFactory"/> both pin the Api role and substitute a job client, so neither starts a Hangfire
/// server and neither reaches <c>WorkerHangfireRegistrar.ScanHangfireRecurringJobs</c> — the one place every
/// <c>IRecurringJob</c> is registered. Nothing in CI had ever fired one.
///
/// <para>So nothing is faked here: the real <c>CodeSpaceBackgroundJobClient</c> registers the recurring schedule,
/// the two real Hangfire servers (control pool + agent pool) fetch from the real Postgres queue, and a triggered
/// tick runs through <c>IJobSafeRunner</c> → the job's own DI scope → the mediator pipeline, exactly as on a
/// worker pod.</para>
///
/// <para>The host gets its OWN Serilog logger, wired to <paramref name="logSink"/>, instead of the process-wide
/// <c>Serilog.Log.Logger</c> that <c>Program.CreateHostBuilder</c>'s bare <c>UseSerilog()</c> would bind. That is
/// what lets a test read back what the pipeline logged — a sweep whose NESTED command rolled back still reports a
/// Succeeded tick, and the rollback line is the only evidence — without mutating global state other test hosts in
/// this process share.</para>
/// </summary>
public sealed class RecurringJobWorkerHostFactory : WebApplicationFactory<CodeSpace.Api.Program>
{
    private readonly ILogEventSink _logSink;
    private readonly string _adminConnectionString;
    private readonly string _testConnectionString;

    public RecurringJobWorkerHostFactory(ILogEventSink logSink)
    {
        _logSink = logSink;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        _adminConnectionString = config.GetValue<string>("TestPostgres:AdminConnectionString")
            ?? throw new InvalidOperationException("TestPostgres:AdminConnectionString must be configured in appsettings.json");

        var prefix = config.GetValue<string>("TestPostgres:TestDatabaseNamePrefix") ?? "codespace_e2e_";
        DatabaseName = $"{prefix}{Guid.NewGuid():N}";
        _testConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = DatabaseName }.ConnectionString;
    }

    /// <summary>The per-run GUID database. Read back by the test to prove the storage it inspects is THIS host's.</summary>
    public string DatabaseName { get; }

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        new DbUpRunner(_testConnectionString).Run();

        // TeamSecretEncryption fail-fasts without this outside Development; pin a deterministic key (same value the
        // sibling factories use).
        Environment.SetEnvironmentVariable("CODESPACE_TEAM_SECRET_MASTER_KEY", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
    }

    /// <summary>Overridden rather than hung off <c>IAsyncLifetime</c> so the GUID database is dropped by a plain <c>await using</c>, on the failure path too.</summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();

        // The Hangfire servers hold pooled connections; a DROP fails while any session is still attached.
        await using (var kill = new NpgsqlCommand(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{DatabaseName}' AND pid <> pg_backend_pid()", conn))
            await kill.ExecuteNonQueryAsync();

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\"", conn);
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
                ["Authentication:Jwt:SymmetricKey"] = TaskLaunchApiFactory.JwtKey,
                ["OAuth:CallbackUrl"] = "http://localhost/api/credentials/oauth/callback",
                // The PROCESSING role, named explicitly. It is also the default, but naming it is the whole point of
                // this fixture: the property under test is that THIS role registers and runs the recurring jobs.
                [HangfireHostingSetting.ConfigurationKey] = nameof(HangfireHosting.Worker),
            });
        });
    }

    /// <summary>
    /// Appended AFTER <c>Program.CreateHostBuilder</c>'s own <c>UseSerilog()</c>, so this host's
    /// <c>ILoggerFactory</c> registration is the last one and wins — every <c>ILogger&lt;T&gt;</c> the pipeline
    /// resolves (including <c>TransactionalBehavior</c>'s) writes to the sink. The test proves that wiring with a
    /// canary line before it trusts the absence of anything.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(_logSink).CreateLogger(), dispose: true);

        return base.CreateHost(builder);
    }
}
