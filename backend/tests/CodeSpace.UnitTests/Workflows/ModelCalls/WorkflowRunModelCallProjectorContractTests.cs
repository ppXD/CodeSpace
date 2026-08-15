using CodeSpace.Core.Jobs;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.Messages.Commands.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.ModelCalls;

[Trait("Category", "Unit")]
public sealed class WorkflowRunModelCallProjectorContractTests
{
    [Fact]
    public void Projection_is_a_scoped_bounded_shadow_contract()
    {
        typeof(IWorkflowRunModelCallProjector).GetInterfaces().Select(value => value.Name).ShouldContain("IScopedDependency");
        typeof(IWorkflowRunModelCallProjector).GetMethods().Select(value => value.Name).ShouldBe(["SweepAsync"]);
        new ProjectWorkflowRunModelCallsCommand().BatchSize.ShouldBe(250);
        new WorkflowRunModelCallProjectionResult(2, 3).TotalChanges.ShouldBe(5);
    }

    [Fact]
    public void Recurring_job_uses_the_generic_job_contract()
    {
        typeof(WorkflowRunModelCallProjectionRecurringJob).GetInterfaces().ShouldContain(typeof(IRecurringJob));
    }
}
