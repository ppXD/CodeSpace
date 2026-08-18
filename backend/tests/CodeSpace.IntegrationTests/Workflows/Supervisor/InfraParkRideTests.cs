using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Deterministic proof (no Postgres, no live model) that <see cref="InfraParkRide"/> tells the three states apart that
/// the live whole-loop gates conflated: a cell held by the engine's OWN model-plane park is PARKED (ride it — a park is
/// not a verdict), a cell that reached a real terminal is SETTLED (judge it), and a park that outlives the ride's budget
/// yields the HONEST INFRA outcome that must never read as a green pass. Plus the ride's REPORT, which the planner
/// gate's live-call floor consumes: whether a park was actually ridden, and the drive time with the pauses netted out.
///
/// <para>SCOPE, stated honestly. This file pins the CLASSIFICATION, the BUDGET and the REPORT. It cannot pin the SQL
/// <c>WHERE</c> that decides ownership on the live path — the read filters by wait kind BEFORE <c>Classify</c> sees a
/// cell, so the non-park rows in the theory below are inputs production cannot produce and prove the second, redundant
/// guard only. That predicate, the newest-wait ordering, the cell join and the production deadline wake are pinned
/// against real Postgres by <see cref="InfraParkRideFlowTests"/>.</para>
/// </summary>
public sealed class InfraParkRideTests
{
    [Theory]
    // ── The engine's OWN model-plane park on a non-terminal cell → PARKED (ride it; a park is not a verdict) ──
    [InlineData(NodeStatus.Suspended, WorkflowWaitKinds.SupervisorInfraPark, ParkedCellState.Parked)]
    [InlineData(NodeStatus.Pending, WorkflowWaitKinds.SupervisorInfraPark, ParkedCellState.Parked)]     // the wake has not re-entered the node yet
    [InlineData(NodeStatus.Running, WorkflowWaitKinds.SupervisorInfraPark, ParkedCellState.Parked)]
    // ── A REAL TERMINAL → SETTLED (judge it) — including over a lingering park wait, which no wake can move ──
    [InlineData(NodeStatus.Success, null, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Failure, null, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Skipped, null, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Success, WorkflowWaitKinds.SupervisorInfraPark, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Failure, WorkflowWaitKinds.SupervisorInfraPark, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Skipped, WorkflowWaitKinds.SupervisorInfraPark, ParkedCellState.Settled)]   // the ONLY row pinning Skipped as terminal — without it the ride would wake a skipped cell until its budget ran out
    // ── A wait this ride does NOT own → SETTLED (the caller's own logic owns an approval card / agent run / self-advance) ──
    [InlineData(NodeStatus.Suspended, WorkflowWaitKinds.Approval, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Suspended, WorkflowWaitKinds.AgentRun, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Suspended, WorkflowWaitKinds.SupervisorDecision, ParkedCellState.Settled)]
    [InlineData(NodeStatus.Suspended, null, ParkedCellState.Settled)]
    public void Tells_the_engines_own_infra_park_apart_from_a_settled_cell(NodeStatus cellStatus, string? pendingWaitKind, ParkedCellState expected)
    {
        var cell = new ParkedCell { CellStatus = cellStatus, PendingWaitKind = pendingWaitKind, NodeId = "planner" };

        InfraParkRide.Classify(cell).ShouldBe(expected,
            customMessage: $"cell={cellStatus}, pendingWait={pendingWaitKind ?? "(none)"} → expected {expected}. "
                         + "A parked cell misread as Settled is the false red this ride exists to remove; a settled cell misread as Parked would ride forever.");
    }

    [Fact]
    public async Task A_park_that_resolves_inside_the_budget_rides_out_and_the_gate_judges_the_finished_run()
    {
        // Parked for the first two reads, then the wake lands and the cell reaches its real terminal — the product
        // working. The ride must return QUIETLY (no throw, no skip) so every assertion downstream still judges the run.
        var reads = new Queue<ParkedCell>(new[] { Parked(), Parked(), Settled() });
        var wakes = 0;

        var ride = await InfraParkRide.RideAsync(() => Task.FromResult(reads.Dequeue()), _ => { wakes++; return Task.CompletedTask; }, maxWakes: 5, wakePause: TimeSpan.Zero);

        wakes.ShouldBe(2, "the ride fires one wake per parked read and STOPS the instant the cell settles — it must not keep waking a finished run");
        reads.ShouldBeEmpty("the settling read is the one that ends the ride");

        ride.Wakes.ShouldBe(2, "the ride must REPORT the wakes it fired — a caller that measures live-call latency needs to know the settling call happened on a re-entry, inside the ride");
        ride.Rode.ShouldBeTrue("a ride that fired a wake actually rode; the caller's own pre-ride timing no longer covers the model call that settled the cell");
    }

    [Fact]
    public async Task A_ride_that_had_nothing_to_do_reports_that_it_never_rode()
    {
        // The healthy-run shape, and the one the live-call floor must still judge at FULL strength: nothing parked, so
        // the caller's own first-walk timing is the whole drive and the ride contributes no credit worth the name.
        var ride = await InfraParkRide.RideAsync(() => Task.FromResult(Settled()), _ => Task.FromException(new InvalidOperationException("a settled cell must never be woken")), maxWakes: 5, wakePause: TimeSpan.Zero);

        ride.Wakes.ShouldBe(0, "a healthy run is ONE read and no wake");
        ride.Rode.ShouldBeFalse("nothing was ridden, so the caller keeps measuring exactly what it measured before this helper existed");
    }

    [Fact]
    public async Task The_reported_drive_time_counts_the_wakes_work_and_EXCLUDES_the_rides_own_pauses()
    {
        // THE reason DriveTime exists. The planner gate feeds `firstWalk + DriveTime` to its live-call floor, so this
        // number decides two false verdicts at once. If it OMITTED the wake's work, a park that resolved would still
        // red the floor (the first walk covers only one fast failed 429/503) — the exact false red this lane removes.
        // If it INCLUDED the pauses, the ride would hand a fake-served planner ~40s of free credit and the floor could
        // never catch the 221ms fake again. So: real wake work counted, deliberate sleeping not.
        var pause = TimeSpan.FromMilliseconds(400);          // slept TWICE (once per wake) → 800ms of pure waiting
        var workPerWake = TimeSpan.FromMilliseconds(60);     // driven TWICE → ~120ms of real engine driving
        var reads = new Queue<ParkedCell>(new[] { Parked(), Parked(), Settled() });

        var ride = await InfraParkRide.RideAsync(() => Task.FromResult(reads.Dequeue()), async _ => await Task.Delay(workPerWake), maxWakes: 5, wakePause: pause);

        ride.Wakes.ShouldBe(2);

        ride.DriveTime.ShouldBeGreaterThanOrEqualTo(workPerWake,
            $"DriveTime was {ride.DriveTime.TotalMilliseconds:0}ms — the ride drove the engine for {workPerWake.TotalMilliseconds:0}ms per wake, and dropping that work leaves the floor timing one FAILED call and red-ing a park that RESOLVED");

        ride.DriveTime.ShouldBeLessThan(pause,
            $"DriveTime was {ride.DriveTime.TotalMilliseconds:0}ms, which reaches into the {(pause * 2).TotalMilliseconds:0}ms the ride SLEPT — counting the ride's own waiting would let a fake-served planner clear the live-call floor for free");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    [InlineData(0)]   // a zero budget reads once and judges — it must still refuse to call an unresolved park settled
    public async Task An_exhausted_budget_yields_the_honest_infra_outcome_never_a_green_pass(int maxWakes)
    {
        var wakes = 0;

        var ex = await Should.ThrowAsync<InfraParkUnresolvedException>(() =>
            InfraParkRide.RideAsync(() => Task.FromResult(Parked()), _ => { wakes++; return Task.CompletedTask; }, maxWakes, wakePause: TimeSpan.Zero));

        wakes.ShouldBe(maxWakes, "the ride spends its WHOLE budget before it gives up — one wake per rung, no more, no fewer");

        ex.Message.ShouldContain(WorkflowWaitKinds.SupervisorInfraPark, Case.Sensitive, "the infra reason must name the park it could not clear, so the job summary is diagnosable");
        ex.Message.ShouldContain("NOT a pass", Case.Sensitive, "an unresolved park is explicitly not-a-pass — skip ≠ pass is this lane's whole discipline");

        // …and it must land on the gate's LOUD non-gating infra route, not on a CapabilityMiss red: the run behaved
        // exactly as designed (park, don't die) and the owner's gateway being down may never red main.
        RealModelGate.IsGatewayInfraFailure(ex).ShouldBeTrue("an unresolved model-plane park routes as infra — a model-capability red here would be the original false red wearing a new name");
        RealModelGate.IsGatewayInfraFailure(new AggregateException(ex)).ShouldBeTrue("the await chain can wrap it");
    }

    [Fact]
    public void The_ride_pauses_for_real_between_wakes_so_a_recovery_can_actually_be_observed()
    {
        // A zero pause would make "riding" a busy-loop that burns the whole budget in one instant — it could never
        // observe a plane coming back, so every blip would still surface as an infra skip instead of a finished run.
        InfraParkRide.WakePause.ShouldBeGreaterThan(TimeSpan.Zero);
        InfraParkRide.MaxWakes.ShouldBeGreaterThan(0, "a zero-wake ride is the skip this helper exists to avoid");
    }

    private static ParkedCell Parked() => new() { CellStatus = NodeStatus.Suspended, PendingWaitKind = WorkflowWaitKinds.SupervisorInfraPark, NodeId = "planner" };

    private static ParkedCell Settled() => new() { CellStatus = NodeStatus.Success, PendingWaitKind = null, NodeId = "planner" };
}
