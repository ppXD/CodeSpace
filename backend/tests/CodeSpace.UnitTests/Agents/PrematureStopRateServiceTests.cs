using System.Text.Json;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>Pins <see cref="PrematureStopRateService.Classify"/> (P4) — the pure, DB-free classification rule mapping one run into the shared <see cref="RunOutcomeBucket"/>, across every projection kind.</summary>
public sealed class PrematureStopRateServiceTests
{
    private static readonly IReadOnlyDictionary<Guid, (string? PayloadJson, string? OutcomeJson)> NoStops = new Dictionary<Guid, (string?, string?)>();

    [Theory]
    [InlineData(WorkflowRunStatus.Pending)]
    [InlineData(WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Suspended)]
    public void A_non_terminal_run_of_any_projection_kind_is_StillInProgress(WorkflowRunStatus status)
    {
        var run = new PrematureStopRateService.RunRow(Guid.NewGuid(), status, TaskProjectionKinds.SingleAgent, DateTimeOffset.UtcNow);

        PrematureStopRateService.Classify(run, NoStops).ShouldBe(RunOutcomeBucket.StillInProgress);
    }

    [Theory]
    [InlineData(TaskProjectionKinds.SingleAgent)]
    [InlineData(TaskProjectionKinds.PlanMapSynth)]
    [InlineData(TaskProjectionKinds.Supervisor)]
    public void A_cancelled_run_of_any_projection_kind_is_Cancelled_even_with_a_stop_decision_present(string projectionKind)
    {
        var runId = Guid.NewGuid();
        var run = new PrematureStopRateService.RunRow(runId, WorkflowRunStatus.Cancelled, projectionKind, DateTimeOffset.UtcNow);
        // Even if a stop decision happens to exist for this run id, Cancelled must win — an operator cancel is
        // never reclassified by reading decision content.
        var stops = new Dictionary<Guid, (string?, string?)> { [runId] = (Payload("no progress"), null) };

        PrematureStopRateService.Classify(run, stops).ShouldBe(RunOutcomeBucket.Cancelled);
    }

    [Fact]
    public void A_single_agent_run_that_reached_Success_is_Succeeded()
    {
        var run = new PrematureStopRateService.RunRow(Guid.NewGuid(), WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent, DateTimeOffset.UtcNow);

        PrematureStopRateService.Classify(run, NoStops).ShouldBe(RunOutcomeBucket.Succeeded);
    }

    [Fact]
    public void A_single_agent_run_that_reached_Failure_is_Degraded()
    {
        var run = new PrematureStopRateService.RunRow(Guid.NewGuid(), WorkflowRunStatus.Failure, TaskProjectionKinds.SingleAgent, DateTimeOffset.UtcNow);

        PrematureStopRateService.Classify(run, NoStops).ShouldBe(RunOutcomeBucket.Degraded);
    }

    [Fact]
    public void A_plan_map_run_that_reached_Failure_is_Degraded()
    {
        var run = new PrematureStopRateService.RunRow(Guid.NewGuid(), WorkflowRunStatus.Failure, TaskProjectionKinds.PlanMapDynamic, DateTimeOffset.UtcNow);

        PrematureStopRateService.Classify(run, NoStops).ShouldBe(RunOutcomeBucket.Degraded);
    }

    [Fact]
    public void A_supervisor_run_with_a_genuine_success_stop_is_Succeeded_even_though_the_stop_carries_no_reason()
    {
        var runId = Guid.NewGuid();
        var run = new PrematureStopRateService.RunRow(runId, WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor, DateTimeOffset.UtcNow);
        // A model-authored success stop carries {outcome, summary} on its OUTCOME, not a {reason} payload.
        var stops = new Dictionary<Guid, (string?, string?)> { [runId] = ("{}", JsonSerializer.Serialize(new { outcome = "completed", summary = "done" })) };

        PrematureStopRateService.Classify(run, stops).ShouldBe(RunOutcomeBucket.Succeeded);
    }

    [Fact]
    public void A_supervisor_run_with_a_forced_stop_is_Degraded_even_though_WorkflowRunStatus_reads_Success()
    {
        // THE core gap this metric exists to close: the supervisor's DAG Terminal node can still return Success
        // even when the supervisor itself was force-stopped by a bound — the run's own coarse status alone would
        // misclassify this as a genuine success.
        var runId = Guid.NewGuid();
        var run = new PrematureStopRateService.RunRow(runId, WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor, DateTimeOffset.UtcNow);
        var stops = new Dictionary<Guid, (string?, string?)> { [runId] = (Payload("no progress"), null) };

        PrematureStopRateService.Classify(run, stops).ShouldBe(RunOutcomeBucket.Degraded, "a forced stop reason must override the misleadingly-clean WorkflowRunStatus.Success");
    }

    [Fact]
    public void A_supervisor_run_with_a_give_up_stop_is_Degraded()
    {
        var runId = Guid.NewGuid();
        var run = new PrematureStopRateService.RunRow(runId, WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor, DateTimeOffset.UtcNow);
        var stops = new Dictionary<Guid, (string?, string?)> { [runId] = ("{}", JsonSerializer.Serialize(new { outcome = "no-decision", summary = "gave up" })) };

        PrematureStopRateService.Classify(run, stops).ShouldBe(RunOutcomeBucket.Degraded);
    }

    [Fact]
    public void A_supervisor_run_with_no_stop_decision_at_all_falls_back_to_its_own_honest_WorkflowRunStatus()
    {
        // Defensive fallback (mirrors SupervisorEvalScorecard.ClassifyByRunStatus): the supervisor node failed
        // outright with no recorded stop decision — read the run's own status rather than defaulting to success.
        var succeeded = new PrematureStopRateService.RunRow(Guid.NewGuid(), WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor, DateTimeOffset.UtcNow);
        var failed = new PrematureStopRateService.RunRow(Guid.NewGuid(), WorkflowRunStatus.Failure, TaskProjectionKinds.Supervisor, DateTimeOffset.UtcNow);

        PrematureStopRateService.Classify(succeeded, NoStops).ShouldBe(RunOutcomeBucket.Succeeded);
        PrematureStopRateService.Classify(failed, NoStops).ShouldBe(RunOutcomeBucket.Degraded);
    }

    private static string Payload(string reason) => JsonSerializer.Serialize(new { reason });
}
