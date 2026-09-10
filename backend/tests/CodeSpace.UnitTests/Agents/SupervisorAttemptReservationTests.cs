using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the DEADLINE a supervisor wave's per-attempt budget reservation carries
/// (<see cref="RealSupervisorActionExecutor.AttemptReservationDeadline"/>). It used to be null, which made the row
/// invisible to <c>IBudgetLedger.ExpireOverdueAsync</c> — that sweep only targets rows carrying a deadline — so a
/// wave orphaned between reserving and staging held the run's cap headroom LIVE forever, with nothing left to
/// settle it. The horizon is the attempt's own maximum wall clock plus a committed grace, and the two constants
/// are pinned (Rule 8): shortening either would start expiring HEALTHY attempts' reservations out from under them.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAttemptReservationTests
{
    [Theory]
    [InlineData(3600, null, 3600)]                      // the default 1h wall clock, no revise rounds
    [InlineData(600, 0, 600)]                           // supervisor-dispatched units pin rounds to an explicit 0
    [InlineData(600, 2, 1800)]                          // each revise round RE-ARMS the timeout: (1 + rounds) × 600
    [InlineData(null, null, 24 * 3600)]                 // no wall clock at all ⇒ the committed horizon, never null
    [InlineData(0, null, 24 * 3600)]                    // a non-positive timeout is no wall clock either
    [InlineData(int.MaxValue, null, 24 * 3600)]         // capped, so an implausible value cannot overflow the arithmetic
    public void An_attempts_reservation_expires_past_its_own_maximum_wall_clock(int? timeoutSeconds, int? reviseRounds, int expectedWallClockSeconds)
    {
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", TimeoutSeconds = timeoutSeconds, MaxReviseRounds = reviseRounds };

        var deadline = RealSupervisorActionExecutor.AttemptReservationDeadline(task);

        var expected = DateTimeOffset.UtcNow.AddSeconds(expectedWallClockSeconds).AddMinutes(RealSupervisorActionExecutor.AttemptReservationGraceMinutes);
        deadline.ShouldBeGreaterThan(expected.AddMinutes(-1), "a HEALTHY attempt's reservation must never expire before its own wall clock plus the grace");
        deadline.ShouldBeLessThan(expected.AddMinutes(1));
    }

    [Fact]
    public void The_attempt_reservation_horizon_constants_are_committed_values()
    {
        // Shortening the grace expires live attempts' reservations mid-run (harmless to the money — Indeterminate
        // still holds the claim — but it turns every long attempt into recovery bookkeeping noise); removing the
        // unbounded horizon puts the forever-live orphan back.
        RealSupervisorActionExecutor.AttemptReservationGraceMinutes.ShouldBe(30);
        RealSupervisorActionExecutor.UnboundedAttemptReservationHorizonHours.ShouldBe(24);
    }

    [Fact]
    public void The_attempt_reservation_kind_is_durable_state_and_pinned()
    {
        // Every existing row carries this literal, and three sites must agree on it: the executor that reserves,
        // the sweep that settles from the tape, and the reconcile pass that closes orphans. A rename reaching only
        // some of them splits the ledger into reservations nothing settles and a sweep that finds nothing.
        BudgetKinds.AgentAttempt.ShouldBe("agent-attempt");
        BudgetKinds.UnbudgetedPrefix.ShouldBe("unbudgeted:");
    }
}
