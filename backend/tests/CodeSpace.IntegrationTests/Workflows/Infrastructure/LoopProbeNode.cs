using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only body node for the flow.loop tests. Each invocation records the (already loop-scope-
/// resolved) <c>value</c> input under its <c>key</c>, in iteration order, and echoes it back as the
/// <c>seen</c> output. Two things it lets a loop test prove rigorously:
///   1. The body actually runs once per iteration AND sees the live <c>loop.*</c> scope (the value it
///      records is whatever <c>{{loop.&lt;var&gt;}}</c> resolved to that pass).
///   2. Loop-variable threading works: with an update ref like <c>"{{loop.acc}}:{{loop.index}}"</c>,
///      the recorded sequence shows each pass building on the previous one's accumulated value.
///
/// Registered into the integration container via <c>PostgresFixture.RegisterTestAssemblyTypes</c> as
/// an <c>INodeRuntime</c> (engine + validator accept "test.loop_probe"); NOT in any IPluginModule, so
/// it never reaches the editor palette.
/// </summary>
public sealed class LoopProbeNode : INodeRuntime
{
    public const string Key = "test.loop_probe";

    private static readonly ConcurrentDictionary<string, List<string>> SeenByKey = new();

    /// <summary>The values this probe saw, in iteration order, for a key. Each test uses a unique key.</summary>
    public static IReadOnlyList<string> SeenFor(string key) => SeenByKey.GetValueOrDefault(key) ?? new List<string>();

    /// <summary>Clear a key before a run so a re-run doesn't accumulate onto a previous pass's record.</summary>
    public static void Reset(string key) => SeenByKey.TryRemove(key, out _);

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Loop probe (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Inputs are resolved against the per-iteration loop scope by the engine, so `value` is
        // whatever {{loop.<var>}} / {{loop.index}} the test wired pointed at THIS pass.
        var key = ReadString(context.Inputs, "key");
        var value = ReadString(context.Inputs, "value");

        SeenByKey.GetOrAdd(key, _ => new List<string>()).Add(value);

        var outputs = new Dictionary<string, JsonElement> { ["seen"] = JsonSerializer.SerializeToElement(value) };
        return Task.FromResult(NodeResult.Ok(outputs));
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
