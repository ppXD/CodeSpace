using System;
using System.Linq;
using CodeSpace.Core;
using CodeSpace.Core.Jobs;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Pins the ONE shape periodic background work is allowed to take: <see cref="IRecurringJob"/>, never
/// <c>IHostedService</c>/<c>BackgroundService</c>.
///
/// <para>Why pin it: a hosted service looks equivalent but differs on four axes that matter here. It runs its own
/// timer in EVERY process — including the public API pod, which under the <c>HangfireHosting=Api</c> role is supposed
/// to process nothing; it runs OUTSIDE the mediator pipeline, so it gets no UnitOfWork, logging or authorization
/// middleware and has to hand-roll a lifetime scope; it is invisible on the Hangfire dashboard, so an operator can
/// neither see its last run nor trigger it; and its error handling is per-author rather than the shared
/// <c>IJobSafeRunner</c>. Two janitors drifted into that shape before, unnoticed, until the API/worker split made the
/// "runs everywhere" part a real problem.</para>
///
/// <para>Scope is the Core assembly — the one that holds business background work. A framework-provided hosted
/// service registered by ASP.NET or Hangfire itself is out of scope and unaffected.</para>
/// </summary>
[Trait("Category", "Unit")]
public class PeriodicWorkIsRecurringJobTests
{
    /// <summary>Matched by full name via reflection so this test needs no compile-time dependency on the hosting abstractions.</summary>
    private const string HostedServiceInterface = "Microsoft.Extensions.Hosting.IHostedService";

    [Fact]
    public void No_business_background_work_is_a_hosted_service()
    {
        var offenders = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.FullName == HostedServiceInterface))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"periodic work must be an {nameof(IRecurringJob)} (job → command → service), not a hosted service. " +
            "A hosted service also runs on the public API pod, skips the mediator pipeline, and is invisible on the " +
            $"Hangfire dashboard. Offenders: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_recurring_job_declares_a_cron_expression_and_a_stable_id()
    {
        var jobs = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IRecurringJob).IsAssignableFrom(t))
            .ToList();

        jobs.ShouldNotBeEmpty("the scan below is meaningless if no recurring job is discovered");

        foreach (var type in jobs)
        {
            // JobId is nameof(the class) by convention — Hangfire indexes by it, so a rename must be a
            // deliberate new id rather than an accidental one that strands the old schedule.
            type.GetProperty(nameof(IRecurringJob.JobId)).ShouldNotBeNull($"{type.Name} must expose JobId");
            type.GetProperty(nameof(IRecurringJob.CronExpression)).ShouldNotBeNull($"{type.Name} must expose CronExpression");
        }
    }
}
