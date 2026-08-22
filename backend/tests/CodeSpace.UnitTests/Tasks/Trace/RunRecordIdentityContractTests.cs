using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Trace;

[Trait("Category", "Unit")]
public sealed class RunRecordIdentityContractTests
{
    [Theory]
    [InlineData(typeof(RunRecordReader))]
    [InlineData(typeof(RunRecordStreamer))]
    public void Trace_readers_use_the_narrow_identity_status_bundle_and_not_full_workflow_detail(Type sourceType)
    {
        var dependencies = sourceType.GetConstructors().ShouldHaveSingleItem().GetParameters().Select(value => value.ParameterType).ToList();

        dependencies.ShouldContain(typeof(IWorkflowRunObservationIdentityBundle));
        dependencies.ShouldNotContain(typeof(IWorkflowService));
    }
}
