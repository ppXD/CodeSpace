using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class FlowWaitActionNodeTests
{
    [Fact]
    public async Task First_pass_parks_on_an_action_wait_keyed_by_the_supplied_token()
    {
        var result = await new FlowWaitActionNode().RunAsync(BuildContext(token: "card-tok-1", resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.Action);
        result.SuspendUntil.CorrelationToken.ShouldBe("card-tok-1",
            customMessage: "the wait must be keyed by the card's token so a click resolves exactly this wait");
    }

    [Fact]
    public async Task First_pass_fails_when_the_token_is_missing()
    {
        var result = await new FlowWaitActionNode().RunAsync(BuildContext(token: null, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("token");
    }

    [Fact]
    public async Task Resumed_pass_surfaces_the_decision_as_outputs()
    {
        var resume = JsonDocument.Parse("""{"action":"approve","by":"11111111-1111-1111-1111-111111111111","comment":"lgtm"}""").RootElement;

        var result = await new FlowWaitActionNode().RunAsync(BuildContext(token: "card-tok-1", resume: resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["action"].GetString().ShouldBe("approve");
        result.Outputs["by"].GetString().ShouldBe("11111111-1111-1111-1111-111111111111");
        result.Outputs["comment"].GetString().ShouldBe("lgtm");
    }

    [Fact]
    public async Task Resumed_pass_defaults_missing_decision_fields_to_empty()
    {
        var result = await new FlowWaitActionNode().RunAsync(BuildContext(token: "card-tok-1", resume: JsonDocument.Parse("{}").RootElement), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["action"].GetString().ShouldBe("");
        result.Outputs["comment"].GetString().ShouldBe("");
    }

    private static NodeRunContext BuildContext(string? token, JsonElement? resume)
    {
        var inputs = new Dictionary<string, JsonElement>();
        if (token != null) inputs["token"] = JsonSerializer.SerializeToElement(token);

        return new NodeRunContext
        {
            Inputs = inputs,
            Config = new Dictionary<string, JsonElement>(),
            RawInputs = JsonDocument.Parse("{}").RootElement,
            RawConfig = JsonDocument.Parse("{}").RootElement,
            Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
            ResumePayload = resume,
        };
    }
}
