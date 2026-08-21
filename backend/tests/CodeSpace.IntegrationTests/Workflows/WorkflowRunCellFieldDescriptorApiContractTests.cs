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
public class WorkflowRunCellFieldDescriptorApiContractTests
{
    [Fact]
    public void Cell_field_descriptor_route_is_additive_team_governed_bounded_and_body_blind()
    {
        var action = typeof(WorkflowRunsController).GetMethod(nameof(WorkflowRunsController.GetCellFields))!;
        action.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("{runId:guid}/cells/fields");
        typeof(IRequireTeamMembership).IsAssignableFrom(typeof(GetWorkflowRunCellFieldsQuery)).ShouldBeTrue();
        GetWorkflowRunCellFieldsQuery.DefaultPageSize.ShouldBe(50);
        GetWorkflowRunCellFieldsQuery.MaximumPageSize.ShouldBe(100);

        typeof(GetWorkflowRunCellFieldsQueryHandler).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe(new[] { typeof(IWorkflowRunCellFieldReader), typeof(ICurrentTeam) });
        typeof(WorkflowRunCellFieldReader).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe(new[] { typeof(CodeSpace.Core.Persistence.Db.CodeSpaceDbContext), typeof(IWorkflowRunViewAdmission) },
                "descriptor metadata must not depend on an artifact/provider byte reader");

        var wireNames = typeof(WorkflowRunCellFieldPage).GetProperties().Concat(typeof(WorkflowRunCellFieldDescriptor).GetProperties())
            .Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "Bytes", "Content", "Value", "ArtifactId", "Config", "Payload" })
            wireNames.ShouldNotContain(forbidden, $"descriptor wire must not expose a {forbidden} body or locator property");
    }
}
