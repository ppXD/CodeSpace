using CodeSpace.Core.Settings.Database;
using Hangfire;
using Hangfire.PostgreSql;
using CoreJobs = CodeSpace.Core.Services.Jobs;

namespace CodeSpace.Api.BackgroundJobs;

/// <summary>
/// Hangfire registration for the API project. <see cref="AddHangfireBackgroundJobs"/>
/// wires up
///   (1) Hangfire's storage (Postgres, sharing CodeSpace's connection string),
///   (2) the Hangfire server (worker pool that drains the queue),
///   (3) <see cref="CoreJobs.IBackgroundJobClient"/> → <see cref="HangfireBackgroundJobClient"/>
///       so the dispatcher's <c>Enqueue</c> calls a real queue in prod and the in-memory
///       recorder in tests.
///
/// <para>The recurring-job registration happens after the host starts via
/// <see cref="HangfireApplicationBuilderExtensions.UseHangfireDashboardAndRecurringJobs"/>
/// because <c>RecurringJob.AddOrUpdate</c> needs a live <c>JobStorage</c> + IServiceProvider
/// to resolve handlers — both only available post-build.</para>
///
/// <para>Hangfire's storage runs <i>its own</i> DbUp-equivalent on first boot, creating tables
/// in a <c>hangfire</c> schema. Operators should NOT manage these tables manually; let
/// Hangfire own them. CodeSpace's own tables stay in the default <c>public</c> schema.</para>
/// </summary>
public static class HangfireServiceCollectionExtensions
{
    /// <summary>Hangfire's storage tables live in their own schema so they don't clutter the public schema.</summary>
    public const string HangfireSchemaName = "hangfire";

    public static IServiceCollection AddHangfireBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = new CodeSpaceConnectionString(configuration).Value;

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = HangfireSchemaName,
                    PrepareSchemaIfNecessary = true,
                    QueuePollInterval = TimeSpan.FromSeconds(2),
                    // The default 5-min invisibility timeout is too long for our use-case;
                    // workflow runs are typically minutes, not hours. Lower so a crashed
                    // worker's job becomes re-claimable faster.
                    InvisibilityTimeout = TimeSpan.FromMinutes(5),
                }));

        services.AddHangfireServer(opts =>
        {
            opts.WorkerCount = Environment.ProcessorCount * 2;   // I/O-bound work
            opts.ServerName = $"codespace-{Environment.MachineName}";
        });

        // Bridge to Core's abstraction. Both lifetimes are managed by Hangfire's own DI seam
        // for the inner client, and our wrapper is scoped-per-request so it can be resolved
        // from any controller/handler without lifecycle mismatch.
        services.AddScoped<CoreJobs.IBackgroundJobClient, HangfireBackgroundJobClient>();

        // IRecurringJob discovery: scan CodeSpace.Core for every implementation + register as
        // transient (Hangfire's activator creates a fresh instance per tick, so transient is
        // correct — singleton would cache resolved dependencies across ticks).
        //
        // We scan the marker interface's assembly so jobs in Core get discovered. API-tier
        // jobs would need the assembly added to this list; for now all jobs are in Core.
        var assemblies = new[] { typeof(CoreJobs.IRecurringJob).Assembly };
        foreach (var assembly in assemblies)
        {
            var jobTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(CoreJobs.IRecurringJob).IsAssignableFrom(t))
                .ToList();

            foreach (var jobType in jobTypes)
            {
                services.AddTransient(jobType);                                              // self-registration so Hangfire's activator can resolve by concrete type
                services.AddTransient(typeof(CoreJobs.IRecurringJob), jobType);              // collection-registration so the post-build loop sees it via GetServices<IRecurringJob>()
            }
        }

        return services;
    }
}
