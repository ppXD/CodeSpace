using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Learning;

[Trait("Category", "Unit")]
public sealed class AgentLessonPromptIdentityTests
{
    [Fact]
    public void Retry_feedback_does_not_move_a_receipt_but_logical_or_runtime_identity_does()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var workUnit = new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 2, UnitId = "api", ContractHash = "contract", RequirementRevision = 7 };
        var original = Request(new AgentTask { Goal = "implement", Harness = "Claude-Code", Model = "MODEL-A", Tools = ["git.diff", "git.status"], WorkUnit = workUnit }, teamId, runId);

        AgentLessonInjector.PromptKey(original with { Task = original.Task with { Goal = "implement\nretry feedback" } }).ShouldBe(AgentLessonInjector.PromptKey(original));
        AgentLessonInjector.PromptKey(original with { Task = original.Task with { Model = "model-b" } }).ShouldNotBe(AgentLessonInjector.PromptKey(original));
        AgentLessonInjector.PromptKey(original with { Task = original.Task with { WorkUnit = workUnit with { RequirementRevision = 8 } } }).ShouldNotBe(AgentLessonInjector.PromptKey(original));
        AgentLessonInjector.PromptKey(original with { Task = original.Task with { Model = " model-a ", Harness = "claude-code", Tools = ["git.status", "GIT.DIFF"] } }).ShouldBe(AgentLessonInjector.PromptKey(original));
    }

    [Fact]
    public void Applicability_requires_exact_known_dimensions_and_the_complete_tool_subset()
    {
        LessonApplicability.AppliesTo(new(null, "MODEL-A", "CLI", ["git.diff", "git.status", "extra"]), ["model-a"], ["cli"], ["git.status", "git.diff"]).ShouldBeTrue();
        LessonApplicability.AppliesTo(new(null, null, "cli", ["git.diff", "git.status"]), ["model-a"], ["cli"], ["git.status"]).ShouldBeFalse();
        LessonApplicability.AppliesTo(new(null, "model-a", "cli", ["git.status"]), ["model-a"], ["cli"], ["git.status", "git.diff"]).ShouldBeFalse();
    }

    private static AgentLessonInjectionRequest Request(AgentTask task, Guid teamId, Guid runId) => new(task, teamId, runId, "agent", "cell");
}
