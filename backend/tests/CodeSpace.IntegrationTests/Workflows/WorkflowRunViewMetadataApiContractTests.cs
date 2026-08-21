using System.Reflection;
using CodeSpace.Api.Controllers;
using CodeSpace.Core.Handlers.QueryHandlers.Workflows;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Trait("Category", "Integration")]
public class WorkflowRunViewMetadataApiContractTests
{
    [Fact]
    public void View_metadata_route_is_additive_team_governed_and_body_blind()
    {
        var action = typeof(WorkflowRunsController).GetMethod(nameof(WorkflowRunsController.GetViewMetadata))!;
        action.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("{runId:guid}/view-metadata");
        typeof(IRequireTeamMembership).IsAssignableFrom(typeof(GetWorkflowRunViewMetadataQuery)).ShouldBeTrue();

        typeof(GetWorkflowRunViewMetadataQueryHandler).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe(new[] { typeof(IWorkflowRunViewMetadataReader), typeof(ICurrentTeam) });

        var forbidden = new[] { "Definition", "Config", "Prompt", "Inputs", "Outputs", "Payload", "Artifact", "ErrorMessage" };
        var wireNames = typeof(WorkflowRunViewMetadata).GetProperties().Concat(typeof(WorkflowRunCellMetadata).GetProperties())
            .Concat(typeof(WorkflowRunCanvasTopology).GetProperties()).Concat(typeof(WorkflowRunCanvasNode).GetProperties())
            .Concat(typeof(WorkflowRunCanvasEdge).GetProperties()).Select(property => property.Name).ToArray();

        foreach (var fragment in forbidden)
            wireNames.ShouldNotContain(name => name.Contains(fragment, StringComparison.Ordinal), $"metadata wire must not expose {fragment} body fields");
    }
}
