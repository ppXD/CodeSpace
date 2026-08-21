using System.Reflection;
using CodeSpace.Api.Controllers;
using CodeSpace.Core.Handlers.QueryHandlers.Workflows;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Trait("Category", "Integration")]
public class WorkflowRunCellFieldRangeApiContractTests
{
    [Fact]
    public void One_field_range_route_is_additive_team_governed_bounded_and_never_exposes_artifact_identity()
    {
        var action = typeof(WorkflowRunsController).GetMethod(nameof(WorkflowRunsController.ReadCellFieldRange))!;
        action.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("{runId:guid}/cells/fields/range");
        typeof(IRequireTeamMembership).IsAssignableFrom(typeof(ReadWorkflowRunCellFieldRangeQuery)).ShouldBeTrue();
        ReadWorkflowRunCellFieldRangeQuery.MaximumPageBytes.ShouldBe(64 * 1024);

        typeof(ReadWorkflowRunCellFieldRangeQueryHandler).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe(new[] { typeof(IWorkflowRunCellFieldRangeReader), typeof(ICurrentTeam) });
        typeof(WorkflowRunCellFieldRangeReader).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe(new[]
            {
                typeof(CodeSpace.Core.Persistence.Db.CodeSpaceDbContext), typeof(IWorkflowRunViewAdmission), typeof(IArtifactRangeReader),
            });

        var wireNames = typeof(WorkflowRunCellFieldRangePage).GetProperties().Select(property => property.Name).ToArray();
        wireNames.ShouldNotContain("ArtifactId");
        wireNames.ShouldNotContain("Value");
        wireNames.ShouldNotContain("Payload");
        wireNames.ShouldContain("IntegrityVerified");
        wireNames.ShouldContain("CompleteJsonValue");
    }
}
