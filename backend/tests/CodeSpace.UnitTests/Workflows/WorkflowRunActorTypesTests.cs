using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 pinning tests for actor-type wire strings. These values land in
/// <c>workflow_run_request.actor_type</c>; renaming any of them silently breaks every
/// existing row plus any downstream consumer that filters on the string (analytics dashboards,
/// audit views).
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowRunActorTypesTests
{
    [Theory]
    [InlineData("user",    nameof(WorkflowRunActorTypes.User))]
    [InlineData("webhook", nameof(WorkflowRunActorTypes.Webhook))]
    [InlineData("system",  nameof(WorkflowRunActorTypes.System))]
    public void ActorType_string_form_is_pinned(string expected, string constantName)
    {
        var actual = typeof(WorkflowRunActorTypes).GetField(constantName)!.GetValue(null) as string;
        actual.ShouldBe(expected,
            $"WorkflowRunActorTypes.{constantName} MUST be wire string '{expected}' — renaming it changes how every " +
            "existing workflow_run_request row reads and breaks analytics filters that match on the literal.");
    }
}
