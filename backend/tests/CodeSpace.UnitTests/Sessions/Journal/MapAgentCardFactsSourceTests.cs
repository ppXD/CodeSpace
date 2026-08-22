using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

[Trait("Category", "Unit")]
public sealed class MapAgentCardFactsSourceTests
{
    [Fact]
    public void Source_uses_bounded_run_metadata_and_not_full_workflow_detail()
    {
        var dependencies = typeof(MapAgentCardFactsSource).GetConstructors().ShouldHaveSingleItem().GetParameters().Select(value => value.ParameterType).ToList();

        dependencies.ShouldContain(typeof(IWorkflowRunViewMetadataReader));
        dependencies.ShouldNotContain(typeof(IWorkflowService));
    }
}
