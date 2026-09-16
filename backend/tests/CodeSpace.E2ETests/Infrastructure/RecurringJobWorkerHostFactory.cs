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
/// <para>The host gets its OWN Serilog logger, wired to the supplied sink, instead of the process-wide
/// <c>Serilog.Log.Logger</c> that <c>Program.CreateHostBuilder</c>'s bare <c>UseSerilog()</c> would bind. That is
/// what lets a test read back what the pipeline logged — a sweep whose NESTED command rolled back still reports a
/// Succeeded tick, and the rollback line is the only evidence. The LOGGER is the part that stays host-local; see
/// below for what this fixture does change process-wide.</para>
///
/// <para><b>Substitutions, stated rather than implied.</b> (1) The host runs as <c>Development</c>, like every
/// sibling fixture: that skips the HTTPS redirect, and it also relaxes two production boot guards —
/// <c>DurableRootsGuard.ThrowIfProductionUnconfigured</c> does not fire, and <c>CodeSpaceModule</c>'s
/// Variables master-key check falls back to a dev key with a warning instead of failing fast. Neither guard is on
/// the job path under test, but neither is being exercised either. (2) <see cref="InitializeAsync"/> sets
/// <c>CODESPACE_TEAM_SECRET_MASTER_KEY</c> in the PROCESS environment (the same deterministic value
/// <see cref="TaskLaunchApiFactory"/> and <see cref="WebhookApiFactory"/> set), which outlives this fixture.</para>
/// </summary>
public sealed class RecurringJobWorkerHostFactory : WebApplicationFactory<CodeSpace.Api.Program>
{
    private readonly ILogEventSink _logSink;
    private readonly string _adminConnectionString;
    private readonly string _testConnectionString;
    private int _dropped;

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

    /// <summary>The per-run GUID database.</summary>
    public string DatabaseName { get; }

    /// <summary>The per-run database's connection string, so the test can open its OWN Hangfire storage handle onto the rows this host's job servers drain, rather than reading through the process-wide <c>JobStorage.Current</c>.</summary>
    public string ConnectionString => _testConnectionString;

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

    /// <summary>
    /// Both disposal paths drop the database, because both are reachable: <c>await using</c> lands here, a plain
    /// <c>using</c> or an xUnit <c>IDisposable</c> teardown lands on <see cref="Dispose(bool)"/>. Hanging the drop
    /// off an explicitly-implemented <c>IAsyncLifetime.DisposeAsync</c> instead — what the sibling fixtures do —
    /// means neither language construct reaches it, and the database is simply left behind.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        DropTestDatabase();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) DropTestDatabase();
    }

    /// <summary>Once-only: the two disposal paths can both run, and a second DROP would race the first rather than no-op.</summary>
    private void DropTestDatabase()
    {
        if (Interlocked.Exchange(ref _dropped, 1) == 1) return;

        using var conn = new NpgsqlConnection(_adminConnectionString);
        conn.Open();

        // The Hangfire servers hold pooled connections; a DROP fails while any session is still attached.
        using (var kill = new NpgsqlCommand($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{DatabaseName}' AND pid <> pg_backend_pid()", conn))
            kill.ExecuteNonQuery();

        using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\"", conn);
        drop.ExecuteNonQuery();
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
