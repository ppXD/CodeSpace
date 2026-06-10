using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Messages.Agents;
using Hangfire.PostgreSql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Hangfire;

/// <summary>
/// Pins the Hangfire Postgres storage contract (<see cref="HangfireRegistrarBase.BuildStorageOptions"/>).
/// The load-bearing assertion is <c>UseSlidingInvisibilityTimeout</c>: agent runs last up to
/// <see cref="AgentTask.TimeoutSeconds"/> (1800s = 30min), far beyond the 5-minute InvisibilityTimeout,
/// so WITHOUT a sliding (auto-renewing) lease Hangfire would re-surface a still-running agent job to a
/// second worker. Lives in IntegrationTests (not UnitTests) only because <see cref="PostgreSqlStorageOptions"/>
/// is a CodeSpace.Api-only package type; it touches no database, hence no Postgres collection.
/// </summary>
[Trait("Category", "Unit")]
public class HangfireStorageOptionsTests
{
    private static readonly PostgreSqlStorageOptions Options = HangfireRegistrarBase.BuildStorageOptions();

    [Fact]
    public void Uses_sliding_invisibility_timeout_so_a_live_long_run_is_never_re_dispatched()
    {
        // Renewing the lease while the worker is alive is what stops a 2nd worker from re-fetching a
        // long agent job mid-flight; removing this regresses to the false-re-dispatch bug.
        Options.UseSlidingInvisibilityTimeout.ShouldBeTrue();
    }

    [Fact]
    public void Sliding_is_required_because_the_lease_window_is_shorter_than_a_max_agent_run()
    {
        // The exact reason sliding is mandatory: the fixed lease window is far below an agent run's
        // wall-clock cap, so a non-sliding timeout would re-surface a live long run to another worker.
        var maxAgentRun = TimeSpan.FromSeconds(new AgentTask { Goal = "x", Harness = "x", Model = "x" }.TimeoutSeconds);

        Options.InvisibilityTimeout.ShouldBeLessThan(maxAgentRun);
        Options.UseSlidingInvisibilityTimeout.ShouldBeTrue();
    }

    [Fact]
    public void Keeps_a_short_invisibility_window_for_fast_crash_recovery_of_short_jobs()
    {
        // A crashed worker's (non-renewed) job must re-become claimable quickly — sliding only protects
        // LIVE long jobs, so the window itself stays short for the short workflow-dispatch/resume jobs.
        Options.InvisibilityTimeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Pins_the_rest_of_the_storage_contract()
    {
        Options.SchemaName.ShouldBe(HangfireRegistrarBase.HangfireSchemaName);
        Options.SchemaName.ShouldBe("hangfire");
        Options.PrepareSchemaIfNecessary.ShouldBeTrue();
        Options.QueuePollInterval.ShouldBe(TimeSpan.FromSeconds(2));
    }
}
