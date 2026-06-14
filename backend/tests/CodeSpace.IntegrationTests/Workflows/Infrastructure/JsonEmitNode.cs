using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only SYNCHRONOUS node that echoes every resolved input straight back as a same-named output —
/// a stateless transform with no shared bookkeeping, so N of them running concurrently inside a
/// <c>flow.map</c> fan-out never race (unlike <c>LoopProbeNode</c>, whose static seen-list is not
/// thread-safe). Two roles in the map tests:
///   1. The PLANNER bridge: an input <c>json</c> = <c>{ "subtasks": [...] }</c> emits output <c>json</c>,
///      which a downstream <c>flow.map</c> binds via <c>{{nodes.planner.outputs.json.subtasks}}</c>.
///   2. A per-element BODY transform: an input <c>value</c> = <c>{{item}}</c> emits output <c>value</c>,
///      which the branch terminal surfaces as that element's <c>results[i]</c>.
///
/// Registered into the integration container via <c>PostgresFixture.RegisterTestAssemblyTypes</c> as an
/// <c>INodeRuntime</c> (engine + validator accept "test.json_emit"); NOT in any IPluginModule, so it
/// never reaches the editor palette.
/// </summary>
public sealed class JsonEmitNode : INodeRuntime
{
    public const string Key = "test.json_emit";

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "JSON emit (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        // Dynamic shape — keys mirror whatever inputs the test wired. additionalProperties:true so the
        // validator treats {{nodes.<emit>.outputs.X}} as dynamic (no strict output-key check).
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "additionalProperties": true }"""),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Inputs are already resolved against the per-element Iteration scope by the engine, so {{item}}
        // / {{index}} carry this branch's element. Echo each verbatim — order/value preserved.
        var outputs = context.Inputs.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        return Task.FromResult(NodeResult.Ok(outputs));
    }
}
