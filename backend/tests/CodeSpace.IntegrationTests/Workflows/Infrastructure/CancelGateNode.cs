using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only node that BLOCKS a wave until the test releases it — the hermetic window for the cooperative-cancel
/// tests. On its first execution it signals <see cref="Started"/> (so the test knows the walk is mid-flight on a
/// node) then awaits <see cref="Release"/> (so the walk is parked inside this node while the test issues an
/// operator cancel). Placed in a linear chain before a downstream probe, it lets a test prove the engine stops
/// firing the REMAINING nodes once the run is cancelled — the audit's P3 cooperative-cancel guarantee.
///
/// Registered into the integration container via <c>PostgresFixture.RegisterTestAssemblyTypes</c> as an
/// <c>INodeRuntime</c> (engine + validator accept "test.cancel_gate"); NOT in any IPluginModule, so it never
/// reaches the editor palette.
/// </summary>
public sealed class CancelGateNode : INodeRuntime
{
    public const string Key = "test.cancel_gate";

    private static readonly ConcurrentDictionary<string, Gate> Gates = new();

    /// <summary>Arm a gate for a unique key BEFORE the run starts; the test awaits <see cref="Gate.Started"/> then sets <see cref="Gate.Release"/>.</summary>
    public static Gate Arm(string key)
    {
        var gate = new Gate();
        Gates[key] = gate;
        return gate;
    }

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Cancel gate (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "additionalProperties": true }"""),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var key = context.Inputs.TryGetValue("gate", out var g) ? g.GetString() : null;

        if (key is not null && Gates.TryGetValue(key, out var gate))
        {
            gate.Started.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return NodeResult.Ok(new Dictionary<string, JsonElement>());
    }

    public sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
