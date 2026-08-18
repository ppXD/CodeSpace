using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Raised when a whole-loop run was STILL parked on the engine's own model-plane park after the ride spent its WHOLE
/// budget — the plane never came back inside the test's window. An HONEST INFRA outcome, never a model verdict:
/// <see cref="RealModelGate.IsGatewayInfraFailure"/> recognises it, so the gate surfaces a LOUD non-gating infra skip
/// (explicitly NOT a pass — nothing was driven to completion) instead of a CapabilityMiss red. Distinct from a gateway
/// timeout only in the surfaced reason; the routing is identical.
/// </summary>
public sealed class InfraParkUnresolvedException : Exception
{
    public InfraParkUnresolvedException(string message) : base(message) { }
}

/// <summary>Whether an infra park is still holding a whole-loop cell, or the ride has nothing left to do with it.</summary>
public enum ParkedCellState
{
    /// <summary>No infra park holds this cell — it reached a REAL TERMINAL (Success / Failure / Skipped), or it is parked on a wait this ride does not own (an approval card, an agent run, a supervisor self-advance). JUDGE it as it stands.</summary>
    Settled,

    /// <summary>The cell is held by the engine's OWN model-plane park (<c>SupervisorInfraPark</c>) and has NOT reached a terminal. This is NOT a verdict — the designed behaviour is that the deadline wake re-enters the same node — so RIDE it.</summary>
    Parked,
}

/// <summary>One read of a whole-loop cell's park state: the projected cell status, plus the run's pending infra-park wait (its id + stored ladder marker) when one exists.</summary>
public sealed record ParkedCell
{
    public required NodeStatus CellStatus { get; init; }
    public required string? PendingWaitKind { get; init; }
    public string NodeId { get; init; } = "";
    public Guid WaitId { get; init; }
    public string? WaitPayloadJson { get; init; }
}

/// <summary>
/// What ONE ride actually did — the report a caller needs because a park MOVES the run's live model call. The
/// successful call that finally settles a parked cell happens INSIDE the ride, on a re-entry, not in the caller's first
/// walk; a caller that measures live-call latency must therefore be told (a) that a ride happened at all and (b) how
/// much wall clock the ride spent DRIVING the engine, with the pauses it deliberately slept EXCLUDED.
/// </summary>
public sealed record ParkRide
{
    /// <summary>Deadline wakes actually fired. 0 = nothing was parked and the ride was a single read.</summary>
    public required int Wakes { get; init; }

    /// <summary>Wall clock the ride spent DRIVING (its reads + its wakes' resume/re-dispatch), MINUS every <see cref="InfraParkRide.WakePause"/> it slept. A caller may add this to its own drive time without the ride's waiting inflating the sum — which is what keeps a live-call floor un-cheatable.</summary>
    public required TimeSpan DriveTime { get; init; }

    /// <summary>True iff the ride actually RODE a park — so the caller's own pre-ride measurement no longer covers the model call that settled the cell.</summary>
    public bool Rode => Wakes > 0;
}

/// <summary>
/// The whole-loop gates' shared MODEL-PLANE-PARK RIDE. A node that calls a model does not fail the run on a transient
/// gateway fault — it parks on the shared exponential ladder (<c>InfraPark</c> / <c>SupervisorInfraPark</c>: 1m → 5m →
/// 15m → 60m inside a 24h window) and the deadline wake RE-ENTERS the same node. A park is BY DESIGN not a failure.
///
/// <para><b>Why this exists.</b> The live gates read a parked cell as a MODEL or WIRING failure, so one flapping runner
/// gateway red-ed two cells at once for a product that behaved exactly as designed. The planner gate read its cell
/// mid-park: a <c>node.suspended</c> record carries no <c>outputs</c> key, the <c>workflow_run_node</c> view's COALESCE
/// yields <c>{}</c>, and the model-stamp assertion failed with "Output keys: []" — a wiring red on a gateway blip. The
/// supervisor arcs saw the same run merely Suspended and scored it a park-short CapabilityMiss. Both are the SAME
/// misreading: a park is neither a verdict nor a fault, it is an unfinished run.</para>
///
/// <para><b>Why RIDING, not skipping.</b> The cheap fix is to skip the assertion when a park is seen. This lane's own
/// workflow file forbids exactly that — "skip ≠ pass would silently pass the acceptance-gate pillar" — and skipping
/// would also throw away the product claim under test: a parked run RESUMES AND FINISHES. So the gate keeps DRIVING the
/// engine through the park's own production wake until the cell settles, and judges only then. A park that resolves is
/// the product working, and the gate still measures the finished run with every assertion intact. A park that outlives
/// the ride's budget is an honest infra outcome that must NOT read green: it throws
/// <see cref="InfraParkUnresolvedException"/>, which routes to the gate's LOUD non-gating infra skip — reported, never
/// a pass. Nothing here classifies AROUND the park; the ride only supplies the wake.</para>
///
/// <para><b>A ride MOVES the run's live model call, so the ride reports itself.</b> The call that finally settles a
/// parked cell happens on a RE-ENTRY, inside the ride — not in the caller's first walk. A caller whose gate measures
/// live-call latency (the planner's live-call floor) would therefore be timing the first walk plus ONE fast failed
/// 4xx/5xx and red-ing a run that parked, resumed and finished. <see cref="ParkRide"/> closes that: it reports whether
/// a park was actually ridden and the wall clock the ride spent DRIVING, with its own pauses subtracted. The caller adds
/// that to its own drive, so the measured window CONTAINS the successful call while the ride's waiting can never lift a
/// fake-served run over the floor.</para>
///
/// <para><b>The wake is the production one.</b> The park's deadline IS its wake, and nothing else resolves it —
/// <see cref="IWorkflowResumeService.ResumeByDeadlineAsync"/> with the wait's OWN stored marker, byte-identical to what
/// the stranded-wait reconciler re-fires. The test must supply it because <see cref="InMemoryBackgroundJobClient"/>
/// only RECORDS a <c>Schedule</c> call, so a park's wake never fires on its own in-process; and it fires at the ride's
/// own short cadence rather than the ladder's real 1m/5m/15m rungs, which are calibrated for a 24h production window
/// and would outlive any CI job. PRECONDITION: the caller's job client has <c>AutoExecute = true</c> (both whole-loop
/// gates do), since the resume re-dispatches the engine through the deferred queue this ride then drains.</para>
/// </summary>
public static class InfraParkRide
{
    /// <summary>How many deadline wakes ONE ride fires before it gives up and reports the honest infra outcome. A committed value (no env toggle): 8 wakes × <see cref="WakePause"/> gives a flapping gateway ~40s of real recovery room, which is small against the whole-loop gate's own per-attempt deadline.</summary>
    public const int MaxWakes = 8;

    /// <summary>Real wall-clock pause before each wake, so the transient fault has a CHANCE to clear before the node re-enters. Must stay non-zero: a zero pause turns the ride into a busy-loop that burns its whole budget in one instant and could never observe a recovery.</summary>
    public static readonly TimeSpan WakePause = TimeSpan.FromSeconds(5);

    /// <summary>A cell that reached a real end — no wake can move it, so a terminal ALWAYS wins over a lingering park wait.</summary>
    private static readonly NodeStatus[] TerminalCellStatuses = { NodeStatus.Success, NodeStatus.Failure, NodeStatus.Skipped };

    /// <summary>Parked iff the engine's OWN model-plane park holds a cell that has not reached a terminal. Every other shape — a real terminal, another wait kind, no wait at all — is Settled: the ride has nothing to do and the caller judges the cell as it stands.</summary>
    public static ParkedCellState Classify(ParkedCell cell) =>
        cell.PendingWaitKind == WorkflowWaitKinds.SupervisorInfraPark && !TerminalCellStatuses.Contains(cell.CellStatus)
            ? ParkedCellState.Parked
            : ParkedCellState.Settled;

    /// <summary>Ride <paramref name="runId"/>'s model-plane park to settlement against the real database. A no-op (one read) when nothing is parked. Throws <see cref="InfraParkUnresolvedException"/> when the park outlives <see cref="MaxWakes"/> wakes.</summary>
    public static Task<ParkRide> RideAsync(PostgresFixture fixture, Guid runId, CancellationToken cancellationToken = default) =>
        RideAsync(fixture, runId, MaxWakes, WakePause, cancellationToken);

    /// <summary>The same REAL read + REAL production wake on a caller-chosen budget and cadence. The integration tier drives this with a ZERO pause, so the SQL park-ownership predicate and the production deadline wake are pinned against real Postgres without a test sleeping through <see cref="WakePause"/>.</summary>
    internal static Task<ParkRide> RideAsync(PostgresFixture fixture, Guid runId, int maxWakes, TimeSpan wakePause, CancellationToken cancellationToken = default) =>
        RideAsync(() => ReadCellAsync(fixture, runId), cell => WakeAsync(fixture, cell, cancellationToken), maxWakes, wakePause, cancellationToken);

    /// <summary>Testable core of the ride — the READ and the WAKE are delegates, so the settle / budget / exhaust logic is provable with no Postgres and no live model. Fires at most <paramref name="maxWakes"/> wakes, then one final read decides: settled ⇒ report, still parked ⇒ the honest infra throw. A zero budget reads once and judges.</summary>
    internal static async Task<ParkRide> RideAsync(Func<Task<ParkedCell>> readCell, Func<ParkedCell, Task> fireWake, int maxWakes, TimeSpan wakePause, CancellationToken cancellationToken = default)
    {
        var budget = Math.Max(0, maxWakes);

        var clock = Stopwatch.StartNew();
        var slept = TimeSpan.Zero;

        for (var wakes = 0; ; wakes++)
        {
            var cell = await readCell().ConfigureAwait(false);

            // The wake re-entered the node and the run moved on — the product working, judged in full. DriveTime nets
            // out the pauses so the caller's live-call floor measures engine driving only, never the ride's waiting.
            if (Classify(cell) == ParkedCellState.Settled) return new ParkRide { Wakes = wakes, DriveTime = clock.Elapsed - slept };

            if (wakes == budget)
                throw new InfraParkUnresolvedException(Unresolved(cell, budget, wakePause));

            slept += await PauseAsync(clock, wakePause, cancellationToken).ConfigureAwait(false);

            await fireWake(cell).ConfigureAwait(false);
        }
    }

    /// <summary>Give the flapping plane a chance to clear before re-entering the node, and report how long that actually slept so <see cref="ParkRide.DriveTime"/> can SUBTRACT it — a caller's live-call floor may never be cleared by the ride's own waiting.</summary>
    private static async Task<TimeSpan> PauseAsync(Stopwatch clock, TimeSpan wakePause, CancellationToken cancellationToken)
    {
        var before = clock.Elapsed;

        await Task.Delay(wakePause, cancellationToken).ConfigureAwait(false);

        return clock.Elapsed - before;
    }

    private static string Unresolved(ParkedCell cell, int wakes, TimeSpan wakePause) =>
        $"node '{cell.NodeId}' was STILL parked on {WorkflowWaitKinds.SupervisorInfraPark} after {wakes} deadline wake(s) over ~{(wakes * wakePause).TotalSeconds:0}s "
      + "— the model plane never came back inside the ride's budget. That is INFRA, not a model verdict, and it is NOT a pass: nothing was driven to completion.";

    /// <summary>Read the run's newest pending infra park plus the projected status of the cell it holds. No park pending ⇒ a Settled cell (there is nothing for the ride to wake).</summary>
    private static async Task<ParkedCell> ReadCellAsync(PostgresFixture fixture, Guid runId)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var wait = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.SupervisorInfraPark)
            .OrderByDescending(w => w.CreatedAt).FirstOrDefaultAsync().ConfigureAwait(false);

        if (wait == null) return new ParkedCell { CellStatus = NodeStatus.Pending, PendingWaitKind = null };

        var cell = await db.WorkflowRunNode.AsNoTracking()
            .FirstOrDefaultAsync(n => n.RunId == runId && n.NodeId == wait.NodeId && n.IterationKey == wait.IterationKey).ConfigureAwait(false);

        return new ParkedCell
        {
            // No MATCHING cell row ⇒ still a park. The park writes node.suspended + the wait in ONE transaction, so a
            // pending park is never a settled cell; and the supervisor's park deliberately keys its wait on its OWN
            // `supervisor#infraN` cell rather than the node's ambient one, so that arm reads through this fallback.
            CellStatus = cell?.Status ?? NodeStatus.Suspended,
            PendingWaitKind = wait.WaitKind,
            NodeId = wait.NodeId,
            WaitId = wait.Id,
            WaitPayloadJson = wait.PayloadJson,
        };
    }

    /// <summary>Fire the park's DEADLINE with the wait's own stored ladder marker — the production wake, exactly as the stranded-wait reconciler re-fires it — then drain the re-dispatch the resume enqueues so the node actually re-enters.</summary>
    private static async Task WakeAsync(PostgresFixture fixture, ParkedCell cell, CancellationToken cancellationToken)
    {
        using (var scope = fixture.BeginScope())
            await scope.Resolve<IWorkflowResumeService>().ResumeByDeadlineAsync(cell.WaitId, cell.WaitPayloadJson ?? "{}", cancellationToken).ConfigureAwait(false);

        using var drain = fixture.BeginScope();
        await drain.Resolve<InMemoryBackgroundJobClient>().WaitForPendingAsync().ConfigureAwait(false);
    }
}
