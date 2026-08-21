using System.Reflection;
using CodeSpace.Api.Controllers;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Trait("Category", "Integration")]
public class WorkflowRunIdentityApiContractTests
{
    [Fact]
    public void Identity_route_is_additive_team_governed_and_metadata_only()
    {
        var action = typeof(WorkflowRunsController).GetMethod(nameof(WorkflowRunsController.GetIdentity))!;

        action.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("{idOrNumber}/identity");
        typeof(IRequireTeamMembership).IsAssignableFrom(typeof(GetWorkflowRunIdentityByRefQuery)).ShouldBeTrue();
        typeof(WorkflowRunIdentity).GetProperties().Select(property => property.Name).Order().ShouldBe(new[] { "Id", "RunNumber", "Status" });
    }
}
