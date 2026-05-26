using CodeSpace.Core.Services.Jobs;
using Hangfire;
using Hangfire.Common;

namespace CodeSpace.Api.BackgroundJobs;

/// <summary>
/// Post-build wiring for Hangfire: dashboard mount + recurring-job discovery.
///
/// <para>The dashboard is mounted at <c>/hangfire</c> behind the
/// <see cref="HangfireDashboardAuthFilter"/> (Admin role required). Operators get the
/// standard Hangfire UI for inspecting in-flight jobs, retry counts, failures, and
/// recurring-job schedules.</para>
///
/// <para>Recurring jobs are <b>discovered, not enumerated</b> — every type that implements
/// <see cref="IRecurringJob"/> across loaded assemblies is registered with Hangfire via
/// <see cref="IRecurringJobManager.AddOrUpdate"/>. Adding a new scheduled task = ship a new
/// <c>IRecurringJob</c> class; zero startup changes. <c>AddOrUpdate</c> is idempotent so
/// re-running this on every boot just updates the cron expression if the job's definition
/// changed since the last deploy.</para>
/// </summary>
public static class HangfireApplicationBuilderExtensions
{
    /// <summary>Mount the <c>/hangfire</c> dashboard + register every <see cref="IRecurringJob"/> implementation.</summary>
    public static IApplicationBuilder UseHangfireDashboardAndRecurringJobs(this IApplicationBuilder app)
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() },
            DashboardTitle = "CodeSpace jobs",
        });

        RegisterRecurringJobs(app.ApplicationServices);

        return app;
    }

    /// <summary>
    /// Resolve every registered <see cref="IRecurringJob"/> and bind it to Hangfire's
    /// scheduler. Each registration carries the concrete TYPE (not the resolved instance);
    /// Hangfire creates a fresh instance per tick via its DI activator, so the recurring
    /// job's dependencies get scoped lifetimes (DbContext, MediatR scope, etc.).
    /// </summary>
    private static void RegisterRecurringJobs(IServiceProvider serviceProvider)
    {
        var recurringJobs = serviceProvider.GetServices<IRecurringJob>().ToList();
        var manager = serviceProvider.GetRequiredService<IRecurringJobManager>();

        foreach (var job in recurringJobs)
        {
            // We capture the CONCRETE type here. Hangfire's activator resolves the type from
            // DI at tick time + invokes Execute() on the freshly-resolved instance. This is
            // what gives us per-tick scope (each tick gets a clean DbContext + handler chain).
            var jobType = job.GetType();
            var executeMethod = jobType.GetMethod(nameof(IRecurringJob.Execute))
                ?? throw new InvalidOperationException($"IRecurringJob impl {jobType.FullName} is missing its Execute method (interface contract broken?)");

            var hangfireJob = new Job(jobType, executeMethod);

            manager.AddOrUpdate(
                recurringJobId: job.JobId,
                job: hangfireJob,
                cronExpression: job.CronExpression,
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }
    }
}
