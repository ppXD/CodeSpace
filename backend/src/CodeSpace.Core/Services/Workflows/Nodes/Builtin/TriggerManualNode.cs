using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// "Run manually" trigger — the on-demand entry node for workflows a person (or an API caller)
/// starts by hand, rather than ones driven by an external webhook/event. It satisfies the
/// engine's "exactly one Trigger node" rule for workflows that have no event source, giving the
/// editor a real Start node instead of forcing an unrelated PR trigger onto a manual flow.
///
/// <para>Unlike the PR triggers, this node subscribes to NOTHING: its manifest sets
/// <see cref="NodeManifest.IsManual"/> = true, so <c>deriveActivations</c> emits no
/// <c>workflow_activation</c> row for it (there is no incoming event to match against). Runs are
/// created through <c>POST /api/workflows/{id}/run</c>, whose operator-supplied payload the
/// engine maps by-name onto the workflow's declared <c>{{input.*}}</c> contract — that is where
/// the per-run, typed values live.</para>
///
/// <para>Like every trigger, it echoes <c>scope.Trigger</c> (the raw run payload) as its outputs
/// so the unified <c>nodes.&lt;id&gt;.outputs.*</c> path also works; downstream nodes normally
/// read the typed per-run values via <c>{{input.&lt;name&gt;}}</c>.</para>
/// </summary>
public sealed class TriggerManualNode : INodeRuntime
{
    public string TypeKey => "trigger.manual";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Manual start",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "play",
        IsManual = true,
        Description = "Starts the workflow on demand (Run now / API). Per-run values come from the workflow's declared inputs.",
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Echo the run payload (scope.Trigger) as outputs, mirroring the event triggers, so a
        // downstream node may use either `trigger.x` or `nodes.<this-id>.outputs.x`. The typed
        // per-run contract is referenced separately via {{input.<name>}}.
        var outputs = context.Scope.Trigger.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(NodeResult.Ok(outputs));
    }
}
