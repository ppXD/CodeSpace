using CodeSpace.Core.Jobs;
using CodeSpace.Core.Services.Jobs;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// The recurring-job set this build schedules: every <see cref="IRecurringJob"/> in <c>CodeSpace.Core</c>, registered
/// with Hangfire as <c>IJobSafeRunner.Run(jobId, jobType)</c>. Asserted once when a worker boots and again by
/// <see cref="RecurringJobScheduleKeeper"/>; both go through here so the two can never schedule different sets.
/// </summary>
internal static class RecurringJobSchedule
{
    /// <summary>Add or update every schedulable job. Returns the jobs it left unscheduled and why; the caller decides whether that is worth a log line.</summary>
    internal static IReadOnlyList<RecurringJobSkip> Assert(IServiceProvider services)
    {
        var backgroundJobClient = services.GetRequiredService<ICodeSpaceBackgroundJobClient>();
        var skipped = new List<RecurringJobSkip>();

        foreach (var type in JobTypes())
        {
            var job = (IRecurringJob)services.GetRequiredService(type);

            if (SkipReasonOf(job) is { } reason)
            {
                skipped.Add(new RecurringJobSkip(type, reason));
                continue;
            }

            backgroundJobClient.AddOrUpdateRecurringJob<IJobSafeRunner>(job.JobId, r => r.Run(job.JobId, type), job.CronExpression, job.TimeZone);
        }

        return skipped;
    }

    private static IEnumerable<Type> JobTypes() =>
        typeof(IRecurringJob).Assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(IRecurringJob).IsAssignableFrom(type));

    /// <summary>A conditionally-registered job (feature-gated, e.g. a flag-OFF lane's reaper) that opts out gets NO recurring entry, so flag-OFF is byte-identical; a job with no cron cannot be scheduled at all.</summary>
    private static RecurringJobSkipReason? SkipReasonOf(IRecurringJob job)
    {
        if (job is IConditionalRecurringJob { ShouldRegister: false }) return RecurringJobSkipReason.ConditionallyOff;

        return string.IsNullOrEmpty(job.CronExpression) ? RecurringJobSkipReason.NoCronExpression : null;
    }
}

internal enum RecurringJobSkipReason
{
    ConditionallyOff,
    NoCronExpression,
}

internal sealed record RecurringJobSkip(Type JobType, RecurringJobSkipReason Reason);
