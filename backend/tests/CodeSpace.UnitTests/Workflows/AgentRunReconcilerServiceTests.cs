using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunReconcilerServiceTests
{
    [Fact]
    public void LivenessWindowEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks any operator who tuned reclaim aggressiveness via env. Hard-pin (Rule 8).
        AgentRunReconcilerService.LivenessWindowEnvVar.ShouldBe("CODESPACE_AGENT_RUN_LIVENESS_WINDOW");
    }

    [Fact]
    public void Adoption_budget_fits_inside_the_sweep_cadence_it_runs_on()
    {
        // The bound this budget exists for: one sweep may re-discover up to BatchSize admitted launches, each waiting
        // on a launch receipt, and the job re-fires EVERY MINUTE. Unbudgeted, that stampede overruns its own cadence
        // and sweeps pile up. Pinned against the job's declared cron rather than a copied number, so a cadence change
        // and a budget change cannot silently disagree.
        new StuckAgentRunReconcilerRecurringJob(null!).CronExpression.ShouldBe("* * * * *",
            customMessage: "this pin assumes a per-minute sweep; a slower cadence would allow a larger adoption budget, so re-derive it deliberately rather than deleting the assertion");

        AgentRunReconcilerService.AdoptionSweepBudget.ShouldBeGreaterThan(TimeSpan.Zero,
            customMessage: "a zero budget would defer every adoptable run for ever, which is the livelock this must not become");
        AgentRunReconcilerService.AdoptionSweepBudget.ShouldBeLessThan(TimeSpan.FromMinutes(1),
            customMessage: "adoption must finish well inside one sweep interval; past it the next sweep starts while this one is still waiting on receipts");
    }
}
