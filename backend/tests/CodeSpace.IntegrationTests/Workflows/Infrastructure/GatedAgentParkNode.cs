using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only node whose FIRST pass holds on a gate (the <see cref="CancelGateNode"/> handshake) and then parks on an
/// AgentRun wait — the suspend an <c>agent.run</c> step produces, from which the engine stages a real agent run. It
/// opens the window of a step still in flight when its run is stopped and continued: the step parks only when the test
/// lets it, after the revived walk has parked the same cell. A resumed pass completes. Registered through
/// <c>PostgresFixture.RegisterTestAssemblyTypes</c>; NOT in any IPluginModule, so it never reaches the editor palette.
/// </summary>
public sealed class GatedAgentParkNode : INodeRuntime
{
    public const string Key = "test.gated_agent_park";

    private static readonly ConcurrentDictionary<string, CancelGateNode.Gate> Gates = new();

    /// <summary>Arm (or re-arm) the gate for a key; the next first pass under that key holds on it until released.</summary>
    public static CancelGateNode.Gate Arm(string key)
    {
        var gate = new CancelGateNode.Gate();
        Gates[key] = gate;
        return gate;
    }

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Gated agent park (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "additionalProperties": true }"""),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (context.ResumePayload.HasValue) return NodeResult.Ok(new Dictionary<string, JsonElement>());

        var key = context.Inputs.TryGetValue("gate", out var g) ? g.GetString() : null;

        if (key is not null && Gates.TryGetValue(key, out var gate))
        {
            gate.Started.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // "wait": "Action" parks a wait with no staged child instead — one a step can still park after its run was stopped,
        // where agent admission refuses a new agent under the terminal run.
        if (context.Inputs.TryGetValue("wait", out var wait) && wait.GetString() == WorkflowWaitKinds.Action)
            return NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.Action, Payload = JsonSerializer.SerializeToElement(new { }) });

        var task = new AgentTask { Goal = "Fix the failing billing tests", Harness = "codex-cli", Model = "gpt-5.3-codex", RunnerKind = "local" };

        return NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.AgentRun, Payload = JsonSerializer.SerializeToElement(task, AgentJson.Options) });
    }
}
