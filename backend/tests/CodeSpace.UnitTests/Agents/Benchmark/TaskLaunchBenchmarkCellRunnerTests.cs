using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// The two PURE decision points inside <see cref="TaskLaunchBenchmarkCellRunner"/> that are cheap to pin without a
/// real DB/engine — which wait kinds the drive loop can advance (the fail-fast fix), and which attempt's model the
/// census reports (the graded-attempt-consistency fix). Pinned directly (InternalsVisibleTo); the full
/// stage→launch→drive→grade pipeline is proven end to end by the integration flow tests.
/// </summary>
[Trait("Category", "Unit")]
public class TaskLaunchBenchmarkCellRunnerTests
{
    [Theory]
    [InlineData(WorkflowWaitKinds.AgentRun, true)]
    [InlineData(WorkflowWaitKinds.SupervisorDecision, true)]
    [InlineData(WorkflowWaitKinds.Action, false)]
    [InlineData(WorkflowWaitKinds.Approval, false)]
    [InlineData(WorkflowWaitKinds.Callback, false)]
    [InlineData(WorkflowWaitKinds.Timer, false)]
    [InlineData(WorkflowWaitKinds.Subworkflow, false)]
    [InlineData(WorkflowWaitKinds.SupervisorAgentWaits, false)]
    [InlineData(WorkflowWaitKinds.Decision, false)]
    [InlineData(WorkflowWaitKinds.SupervisorInfraPark, false)]
    public void Only_AgentRun_and_SupervisorDecision_waits_are_advanceable(string waitKind, bool expected)
    {
        // Exhaustive over all 10 WorkflowWaitKinds constants (Rule 8-style pin): everything else (ask_human's
        // Action, an Approval/Callback/Timer/Subworkflow/Decision park, a SupervisorAgentWaits suspend marker, or
        // a SupervisorInfraPark model-plane-outage park) is a wait this drive loop has no seam for —
        // DriveToTerminalAsync must fail fast on it instead of polling until its deadline. A new wait kind added
        // later has no InlineData here and so is silently treated as advanceable=false by DriveOnePendingWaveAsync
        // (fail-fast, never a silent hang) until this Theory is deliberately widened.
        TaskLaunchBenchmarkCellRunner.IsAdvanceableWaitKind(waitKind).ShouldBe(expected);
    }

    [Theory]
    [InlineData("claude-first-wire", null, "claude-first-wire")]                    // the graded (last) attempt reported none ⇒ falls back to the earlier attempt that did
    [InlineData("claude-first-wire", "claude-graded-wire", "claude-graded-wire")]    // the graded attempt reported its OWN model ⇒ that wins — it is the tree BuildResult's status/exit-reason are also read off
    [InlineData(null, null, null)]                                                   // neither attempt reported one ⇒ unknown stays unknown, never fabricated
    public void ObservedModel_prefers_the_graded_attempts_own_report_falling_back_to_an_earlier_attempt(string? firstModel, string? gradedModel, string? expected)
    {
        var attempts = new[] { Attempt(firstModel), Attempt(gradedModel) };

        TaskLaunchBenchmarkCellRunner.ObservedModelOf(attempts).ShouldBe(expected);
    }

    [Fact]
    public void A_multi_branch_launch_only_claims_one_producer_wire_identity_when_every_observation_agrees()
    {
        var rowId = Guid.NewGuid();
        var selection = new BenchmarkAgentSelection { ModelCredentialModelId = rowId, Model = "configured-alias" };

        TaskLaunchBenchmarkCellRunner.ProducerModelOf(selection, [Attempt("wire-a"), Attempt("WIRE-A")]).ShouldBe(new ReviewModelIdentity { ModelCredentialModelId = rowId, ConfiguredModel = "configured-alias", ObservedModel = "wire-a" });
        TaskLaunchBenchmarkCellRunner.ProducerModelOf(selection, [Attempt("wire-a"), Attempt("wire-b")]).ObservedModel.ShouldBeNull("a union of work from different backing models has no single producer identity and cannot establish judge independence");
    }

    [Fact]
    public void The_graded_workspace_is_checked_under_its_producers_posture_only_when_every_attempt_agrees_on_it()
    {
        var standard = Task(AgentAutonomyLevel.Standard);
        var trusted = Task(AgentAutonomyLevel.Trusted);

        TaskLaunchBenchmarkCellRunner.GradedPosture([WithTask(standard), WithTask(standard)]).ShouldBe(AcceptanceGradingPosturePolicy.For(standard), "one producer posture — the check runs under it");
        TaskLaunchBenchmarkCellRunner.GradedPosture([WithTask(standard), WithTask(trusted)]).ShouldBeNull("a union of work from different postures has no single one to run it under — the grade fails closed");
        TaskLaunchBenchmarkCellRunner.GradedPosture([WithTask(standard), WithTaskJson("not json")]).ShouldBeNull("an attempt whose task cannot be read says nothing about what it was allowed — fail closed");

        static AgentTask Task(AgentAutonomyLevel tier) => new() { Goal = "g", Harness = "test", Autonomy = tier, Permissions = AgentAutonomyPolicy.Derive(tier) };
        static AgentRun WithTask(AgentTask task) => WithTaskJson(System.Text.Json.JsonSerializer.Serialize(task, AgentJson.Options));

        static AgentRun WithTaskJson(string taskJson)
        {
            var attempt = Attempt(null);
            attempt.TaskJson = taskJson;
            return attempt;
        }
    }

    private static AgentRun Attempt(string? model) => new()
    {
        Id = Guid.NewGuid(),
        Status = AgentRunStatus.Succeeded,
        ResultJson = System.Text.Json.JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = model }, AgentJson.Options),
    };
}
