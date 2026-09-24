using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The one rule the engine's replay readers share (<see cref="WorkflowEngine.InjectWaitAnswers"/>): only a RESOLVED wait
/// carries an answer. A stop's teardown closes a pending wait as DISCARDED while its payload still holds the step's own
/// REQUEST. It used to write Resolved there, so Continue handed each parked step its request as the answer — a capped
/// agent read its own task and failed "cumulative spend is missing", an approval read approved=false with no approver, a
/// sleep skipped its timer. Each case runs a real consumer's FIRST pass to get the exact request the engine stores on the
/// wait, replays that row the way the teardown leaves it now and the way it used to, and re-runs the step on each.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowWaitReplayTests
{
    private const string NodeId = "step";

    [Theory]
    [InlineData("agent.run")]
    [InlineData("flow.wait_approval")]
    [InlineData("flow.sleep")]
    [InlineData("flow.decision")]
    [InlineData("flow.wait_action")]
    [InlineData("flow.wait_callback")]
    [InlineData("flow.subworkflow")]
    public async Task A_discarded_wait_replays_as_no_answer_so_the_step_parks_again(string typeKey)
    {
        var (node, context) = FirstPassOf(typeKey);

        var firstPass = await node.RunAsync(context, CancellationToken.None);
        firstPass.Status.ShouldBe(NodeStatus.Suspended, $"precondition: the step parks on its first pass ({firstPass.Error})");
        var request = firstPass.SuspendUntil!.Payload;

        var discarded = Replay(WorkflowWaitStatuses.Discarded, request);
        discarded.ShouldBeNull("a discarded wait holds only the step's request — no replay reader hands it back as the answer");

        var rerun = await node.RunAsync(context with { ResumePayload = discarded }, CancellationToken.None);
        rerun.Status.ShouldBe(NodeStatus.Suspended, "the step found no answer and parked again, exactly as a first run would");
        rerun.SuspendUntil!.Kind.ShouldBe(firstPass.SuspendUntil.Kind);

        var asTheOldTeardownClosedIt = await node.RunAsync(context with { ResumePayload = Replay(WorkflowWaitStatuses.Resolved, request) }, CancellationToken.None);
        asTheOldTeardownClosedIt.Status.ShouldNotBe(NodeStatus.Suspended, "under the status the old teardown wrote, the same request WAS consumed as the answer — the defect the rule exists for");
    }

    [Theory]
    [InlineData(WorkflowWaitStatuses.Pending, false)]
    [InlineData(WorkflowWaitStatuses.Discarded, false)]
    [InlineData(WorkflowWaitStatuses.Resolved, true)]
    public void Only_a_resolved_wait_carries_an_answer(string status, bool replayed)
    {
        Replay(status, Json("""{"approved":true,"by":"u1"}""")).HasValue.ShouldBe(replayed);
    }

    [Fact]
    public void A_resolved_wait_with_no_payload_still_resumes_its_step()
    {
        // A fired timer resolves with no payload; it must still replay as an (empty) answer, or flow.sleep would park forever.
        Replay(WorkflowWaitStatuses.Resolved, payload: null)!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void Rows_apply_in_the_given_order_so_the_freshest_resolution_wins_the_slot()
    {
        var slots = new Dictionary<string, JsonElement>();

        WorkflowEngine.InjectWaitAnswers(new[] { Wait(WorkflowWaitStatuses.Resolved, Json("""{"answer":"old"}""")), Wait(WorkflowWaitStatuses.Resolved, Json("""{"answer":"new"}""")) }, slots);

        slots[NodeId].GetProperty("answer").GetString().ShouldBe("new");
    }

    private static JsonElement? Replay(string status, JsonElement? payload)
    {
        var slots = new Dictionary<string, JsonElement>();

        WorkflowEngine.InjectWaitAnswers(new[] { Wait(status, payload) }, slots);

        return slots.TryGetValue(NodeId, out var answer) ? answer : null;
    }

    private static WorkflowRunWait Wait(string status, JsonElement? payload) => new()
    {
        Id = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        NodeId = NodeId,
        WaitKind = WorkflowWaitKinds.Approval,
        Token = "token",
        Status = status,
        PayloadJson = payload?.GetRawText(),
    };

    // Each consumer with the config / inputs its first pass needs in order to park.
    private static (INodeRuntime Node, NodeRunContext Context) FirstPassOf(string typeKey) => typeKey switch
    {
        "agent.run" => (new AgentCodeNode(), Context("""{"goal":"Fix the failing billing tests","harness":"codex-cli","maxCostUsd":1}""")),
        "flow.wait_approval" => (new FlowWaitApprovalNode(), Context("""{"prompt":"ship it?"}""")),
        "flow.sleep" => (new FlowSleepNode(), Context("""{"seconds":60}""")),
        "flow.decision" => (new FlowDecisionNode(), Context("""{"question":"which way?"}""")),
        "flow.wait_action" => (new FlowWaitActionNode(), Context("{}", inputs: """{"token":"card-token"}""")),
        "flow.wait_callback" => (new FlowWaitCallbackNode(), Context("{}")),
        "flow.subworkflow" => (new FlowSubworkflowNode(), Context($$"""{"workflowId":"{{Guid.NewGuid()}}"}""")),
        _ => throw new ArgumentOutOfRangeException(nameof(typeKey), typeKey, "no first-pass fixture for this consumer"),
    };

    private static NodeRunContext Context(string config, string inputs = "{}") => new()
    {
        NodeId = NodeId,
        Config = Bag(config),
        Inputs = Bag(inputs),
        RawConfig = Json(config),
        RawInputs = Json(inputs),
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };

    private static IReadOnlyDictionary<string, JsonElement> Bag(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
