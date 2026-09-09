using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Learning;

[Trait("Category", "Unit")]
public sealed class PlannerLessonMemoryTests
{
    [Fact]
    public void Identical_prompt_identity_is_stable_but_a_real_revision_gets_a_distinct_receipt()
    {
        var request = Request();
        var taskNeed = PlannerLessonMemory.TaskNeed(request);

        PlannerLessonMemory.PromptKey(request, taskNeed).ShouldBe(PlannerLessonMemory.PromptKey(request, taskNeed));

        var revised = request with { ReviewerCritique = "cover the recovery path" };
        PlannerLessonMemory.PromptKey(revised, PlannerLessonMemory.TaskNeed(revised)).ShouldNotBe(PlannerLessonMemory.PromptKey(request, taskNeed));
    }

    [Fact]
    public void Node_and_repository_identity_prevent_cross_prompt_receipt_adoption()
    {
        var request = Request();
        var key = PlannerLessonMemory.PromptKey(request, PlannerLessonMemory.TaskNeed(request));
        var otherNode = request with { NodeId = "other-plan" };
        var otherRepository = request with { RepositoryId = Guid.NewGuid() };

        PlannerLessonMemory.PromptKey(otherNode, PlannerLessonMemory.TaskNeed(otherNode)).ShouldNotBe(key);
        PlannerLessonMemory.PromptKey(otherRepository, PlannerLessonMemory.TaskNeed(otherRepository)).ShouldNotBe(key);
    }

    private static WorkflowPlanRequest Request() => new() { TeamId = Guid.NewGuid(), WorkflowRunId = Guid.NewGuid(), NodeId = "plan", RepositoryId = Guid.NewGuid(), TaskText = "repair the run", TaskGoal = "repair the run" };
}
