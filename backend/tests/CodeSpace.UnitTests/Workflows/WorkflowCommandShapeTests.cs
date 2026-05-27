using System.Reflection;
using CodeSpace.Messages.Commands.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the Rule 17 route-merge contract on commands that ship a JSON body. Any of these
/// route-id properties going <c>required</c> would re-introduce the "Save returns 400"
/// regression — model binding 400-fails the body before the controller's
/// `command with { WorkflowId = routeId }` merge can run.
///
/// If you ever NEED to mark one required, you also have to invent a separate `…Input`
/// DTO without the id, bind THAT from body, and construct the command manually. This
/// test exists so that future-you sees the breakage immediately, not when a user clicks
/// Save and sees a cryptic 400.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowCommandShapeTests
{
    [Theory]
    [InlineData(typeof(UpdateWorkflowCommand), nameof(UpdateWorkflowCommand.WorkflowId))]
    [InlineData(typeof(SetWorkflowEnabledCommand), nameof(SetWorkflowEnabledCommand.WorkflowId))]
    [InlineData(typeof(RunWorkflowManuallyCommand), nameof(RunWorkflowManuallyCommand.WorkflowId))]
    public void Route_merged_id_must_not_be_required(Type commandType, string propertyName)
    {
        var prop = commandType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        prop.ShouldNotBeNull($"{commandType.Name}.{propertyName} is missing");

        // C# `required` lowers to a RequiredMemberAttribute on the setter. If THAT exists,
        // [ApiController] model binding will 400-reject any body that doesn't include the
        // property — which is the entire problem.
        var hasRequired = prop!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null;
        hasRequired.ShouldBeFalse(
            $"{commandType.Name}.{propertyName} is `required` — this re-introduces the Save 400 bug. " +
            "Route-merged ids must be non-required so the controller's `command with {{ Id = routeId }}` " +
            "merge can run BEFORE binding validation rejects the body.");
    }
}
