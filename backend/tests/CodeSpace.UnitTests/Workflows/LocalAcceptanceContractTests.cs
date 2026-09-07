using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class LocalAcceptanceContractTests
{
    [Fact]
    public void An_authored_empty_contract_still_requires_a_verdict()
    {
        AgentAcceptanceContract.RequiresGrade(new AgentTask { Harness = "test", Goal = "verify", Acceptance = new SupervisorAcceptanceSpec { Command = [] } }).ShouldBeTrue();
        AgentAcceptanceContract.RequiresGrade(new AgentTask { Harness = "test", Goal = "verify", Acceptance = new SupervisorAcceptanceSpec { Command = ["  "] } }).ShouldBeTrue();
        AgentAcceptanceContract.RequiresGrade(new AgentTask { Harness = "test", Goal = "verify" }).ShouldBeFalse();
    }

    [Fact]
    public void A_file_obligation_never_becomes_a_shell_hook_and_invalid_argv_still_requires_final_grading()
    {
        var task = new AgentTask { Harness = "test", Goal = "verify", Acceptance = new SupervisorAcceptanceSpec { Kind = CodeSpace.Messages.Agents.Benchmark.BenchmarkGradingKind.ArtifactPresent, Command = ["report.txt"] } };
        InLoopAcceptanceHook.AppliesTo(task).ShouldBeFalse();
        var invalid = task with { Acceptance = new SupervisorAcceptanceSpec { Command = ["", "true"] } };
        InLoopAcceptanceHook.AppliesTo(invalid).ShouldBeFalse();
        AgentAcceptanceContract.RequiresGrade(invalid).ShouldBeTrue();
    }

    [Fact]
    public void A_model_can_preserve_explicit_literal_oracle_paths_in_the_runtime_contract()
    {
        var json = JsonDocument.Parse("""{"goal":"verify","subtasks":[{"id":"check","title":"Check","instruction":"verify","acceptance":{"formatVersion":2,"kind":"TestsPass","argv":["sh","checks/verify.sh"],"oraclePaths":["checks/verify.sh"]}}]}""");
        var acceptance = LlmWorkflowPlanner.Deserialize(json.RootElement).Subtasks.Single().Acceptance!;
        var serialized = JsonSerializer.SerializeToElement(acceptance, AgentJson.Options);
        serialized.GetProperty("oraclePaths")[0].GetString().ShouldBe("checks/verify.sh");
        PlannerSchema.ResponseSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("items").GetProperty("properties").GetProperty("acceptance").GetProperty("properties").GetProperty("oraclePaths").GetProperty("type").GetString().ShouldBe("array");
    }

    [Fact]
    public void Legacy_contract_bytes_do_not_grow_absent_oracle_metadata()
    {
        const string json = """{"command":["sh","-c","exit 0"]}""";
        JsonSerializer.Serialize(JsonSerializer.Deserialize<SupervisorAcceptanceSpec>(json, AgentJson.Options), AgentJson.Options).ShouldBe(json);
    }

    [Theory]
    [InlineData(GradeFailureClass.Environment)]
    [InlineData(GradeFailureClass.GraderFault)]
    [InlineData(GradeFailureClass.SpecIncomplete)]
    public void Typed_unknown_verdicts_cannot_buy_model_revision_even_when_the_display_text_looks_like_a_test_failure(GradeFailureClass failureClass)
    {
        var task = new AgentTask { Goal = "verify", Harness = "test" };
        var claimed = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "agent-failed", ChangedFiles = ["report.txt"] };
        var graded = claimed with { ExitReason = "acceptance-failed", AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1", AcceptanceFailureClass = failureClass };
        AgentRunExecutor.ReviseReasonFor(task, graded).ShouldBeNull();
        AgentRunExecutor.FoldSelfReportedFailureGrade(claimed, graded).AcceptancePassed.ShouldBeNull();
    }

    [Fact]
    public void The_new_result_failure_class_is_absent_on_legacy_results_and_roundtrips_when_present()
    {
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };
        JsonSerializer.Serialize(result, AgentJson.Options).ShouldNotContain("acceptanceFailureClass");
        var failed = result with { AcceptanceFailureClass = GradeFailureClass.SpecIncomplete };
        JsonSerializer.Deserialize<AgentRunResult>(JsonSerializer.Serialize(failed, AgentJson.Options), AgentJson.Options)!.AcceptanceFailureClass.ShouldBe(GradeFailureClass.SpecIncomplete);
    }
}
