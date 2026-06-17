using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only SIDE-EFFECTING node that records every <c>RunAsync</c> invocation against a config-supplied
/// <c>key</c> and emits the post-increment count as output <c>n</c>. It is the crown-jewel discriminator for
/// from-node rerun: a node that is REUSED (pre-seeded from the original) never re-enters <c>RunAsync</c>, so
/// its counter stays put — while a node that is genuinely RE-RUN bumps it. Asserting the counter is unchanged
/// across a rerun is hard proof the upstream output was carried forward, NOT recomputed.
///
/// <para><see cref="NodeManifest.IsSideEffecting"/> is <c>true</c> on purpose: the rerun side-effect gate
/// FORBIDS an effectful node inside the re-run closure but ALLOWS one upstream (kept + reused). Placing this
/// probe upstream of the rerun target exercises exactly that allow-path and lets the counter prove the reuse.
/// A rerun whose closure CONTAINS the probe is the matching refuse-path.</para>
///
/// <para>Registered into the integration container via <c>PostgresFixture.RegisterTestAssemblyTypes</c> as an
/// <c>INodeRuntime</c> (engine + validator accept "test.mutating_probe"); NOT in any IPluginModule, so it never
/// reaches the editor palette. The counter is static because the node is a DI singleton; keying by config
/// <c>key</c> keeps concurrent / repeated tests independent.</para>
/// </summary>
public sealed class MutatingProbeNode : INodeRuntime
{
    public const string Key = "test.mutating_probe";

    private static readonly ConcurrentDictionary<string, int> ExecutionsByKey = new();

    /// <summary>How many times RunAsync actually executed for a key — 0 after a pure reuse, 1 after one real run.</summary>
    public static int ExecutionsFor(string key) => ExecutionsByKey.GetValueOrDefault(key);

    /// <summary>Reset a key's counter so a test starts from a known baseline (the static dict outlives a single run).</summary>
    public static void Reset(string key) => ExecutionsByKey.TryRemove(key, out _);

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Mutating probe (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        // Dynamic — additionalProperties:true so the validator treats {{nodes.<probe>.outputs.n}} as dynamic.
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "additionalProperties": true }"""),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var key = ReadString(context.Config, "key");
        var n = ExecutionsByKey.AddOrUpdate(key, 1, (_, prev) => prev + 1);

        var outputs = new Dictionary<string, JsonElement> { ["n"] = JsonSerializer.SerializeToElement(n) };
        return Task.FromResult(NodeResult.Ok(outputs));
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
