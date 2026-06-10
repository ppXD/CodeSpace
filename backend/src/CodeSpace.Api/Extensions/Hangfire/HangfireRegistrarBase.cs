using CodeSpace.Core.Settings.Database;
using Hangfire;
using Hangfire.PostgreSql;
using Newtonsoft.Json;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// Shared storage + serializer + filter configuration. Concrete registrars subclass and
/// override <see cref="ApplyHangfire"/> to mount the dashboard and scan recurring jobs.
/// </summary>
public class HangfireRegistrarBase : IHangfireRegistrar
{
    /// <summary>Hangfire's storage tables live in their own schema so they don't pollute the public schema.</summary>
    public const string HangfireSchemaName = "hangfire";

    public virtual void RegisterHangfire(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = new CodeSpaceConnectionString(configuration).Value;

        services.AddHangfire((sp, c) =>
        {
            c.UseSimpleAssemblyNameTypeSerializer();
            c.UseMaxArgumentSizeToRender(int.MaxValue);
            // Disable Hangfire's automatic retry: our workflow_run state machine + reconciler
            // own the retry semantics. Hangfire's own retry would conflict with the dispatcher
            // CAS contract (re-firing a job whose row is no longer Enqueued).
            c.UseFilter(new AutomaticRetryAttribute { Attempts = 0, LogEvents = false });
            c.UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString), BuildStorageOptions());
            c.UseSerializerSettings(new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            });
        });
    }

    /// <summary>
    /// The durability-critical Postgres storage knobs, factored out so they are a single pinned
    /// contract (asserted by HangfireStorageOptionsTests).
    ///
    /// <para><see cref="PostgreSqlStorageOptions.UseSlidingInvisibilityTimeout"/> is the load-bearing
    /// one: while a worker is alive it renews the fetch lease, so a long agent run (up to ~30min) is
    /// never falsely re-dispatched to a second worker mid-flight. A worker that actually dies stops
    /// renewing, so its job still re-becomes claimable after <see cref="PostgreSqlStorageOptions.InvisibilityTimeout"/>
    /// — preserving fast crash recovery for the short workflow-dispatch/resume jobs. WITHOUT sliding,
    /// the fixed 5-minute timeout would re-surface any job running longer than it, which agent runs do
    /// (the AgentRunExecutor Queued→Running CAS backstops the double-dispatch, but the lease must never
    /// trigger it in the first place).</para>
    /// </summary>
    public static PostgreSqlStorageOptions BuildStorageOptions() => new()
    {
        // Storage tables live in their own schema so they don't pollute the public schema.
        SchemaName = HangfireSchemaName,
        PrepareSchemaIfNecessary = true,
        QueuePollInterval = TimeSpan.FromSeconds(2),
        // Window after which a NON-renewed (crashed-worker) job re-becomes claimable — fast recovery
        // for short jobs. Long live jobs never hit it because the lease slides (below).
        InvisibilityTimeout = TimeSpan.FromMinutes(5),
        UseSlidingInvisibilityTimeout = true,
    };

    public virtual void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
    }
}
