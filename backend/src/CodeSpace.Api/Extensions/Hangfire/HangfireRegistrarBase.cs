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
            c.UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = HangfireSchemaName,
                    PrepareSchemaIfNecessary = true,
                    QueuePollInterval = TimeSpan.FromSeconds(2),
                    // Lower than Hangfire's default 5min: matches our typical job duration.
                    // A crashed worker's job becomes re-claimable in 5min instead of 30min.
                    InvisibilityTimeout = TimeSpan.FromMinutes(5),
                });
            c.UseSerializerSettings(new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            });
        });
    }

    public virtual void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
    }
}
