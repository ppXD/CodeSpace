using CodeSpace.Core.Constants;
using CodeSpace.Core.Jobs;
using CodeSpace.Core.Services.Jobs;
using Hangfire;
using Serilog;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// Concrete registrar that adds the Hangfire server, mounts the dashboard, and scans every
/// <see cref="IRecurringJob"/> implementation into the recurring-job scheduler through
/// <see cref="IJobSafeRunner"/>.
/// </summary>
public class CodeSpaceHangfireRegistrar : HangfireRegistrarBase
{
    public override void RegisterHangfire(IServiceCollection services, IConfiguration configuration)
    {
        base.RegisterHangfire(services, configuration);

        services.AddHangfireServer(opt =>
        {
            opt.WorkerCount = Environment.ProcessorCount * 2;
            opt.ServerName = $"codespace-{Environment.MachineName}";
            opt.Queues = new[] { HangfireConstants.DefaultQueue };
        });
    }

    public override void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
        base.ApplyHangfire(app, configuration);

        ScanHangfireRecurringJobs(app);

        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() },
            DashboardTitle = "CodeSpace jobs",
        });
    }

    /// <summary>
    /// Resolve every <see cref="IRecurringJob"/> from DI and register each with Hangfire's
    /// scheduler. The registered call is <c>IJobSafeRunner.Run(jobId, jobType)</c> — Hangfire
    /// serialises that call; on each tick a fresh lifetime scope resolves the concrete job
    /// type from DI + invokes <see cref="IJob.Execute"/> with <c>JobId</c> in the log context.
    /// </summary>
    private static void ScanHangfireRecurringJobs(IApplicationBuilder app)
    {
        var backgroundJobClient = app.ApplicationServices.GetRequiredService<ICodeSpaceBackgroundJobClient>();

        var recurringJobTypes = typeof(IRecurringJob).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IRecurringJob).IsAssignableFrom(type))
            .ToList();

        foreach (var type in recurringJobTypes)
        {
            var job = (IRecurringJob)app.ApplicationServices.GetRequiredService(type);

            if (string.IsNullOrEmpty(job.CronExpression))
            {
                Log.Error("Recurring job cron expression empty, {Job}", job.GetType().FullName);
                continue;
            }

            backgroundJobClient.AddOrUpdateRecurringJob<IJobSafeRunner>(job.JobId, r => r.Run(job.JobId, type), job.CronExpression, job.TimeZone);
        }
    }
}
