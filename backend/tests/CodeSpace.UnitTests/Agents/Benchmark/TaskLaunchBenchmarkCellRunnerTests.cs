using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
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
    public void Only_AgentRun_and_SupervisorDecision_waits_are_advanceable(string waitKind, bool expected)
    {
        // Everything else (ask_human's Action, an Approval/Callback/Timer/Subworkflow park) is a wait this drive
        // loop has no seam for — DriveToTerminalAsync must fail fast on it instead of polling until its deadline.
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

    private static AgentRun Attempt(string? model) => new()
    {
        Id = Guid.NewGuid(),
        Status = AgentRunStatus.Succeeded,
        ResultJson = System.Text.Json.JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = model }, AgentJson.Options),
    };
}
