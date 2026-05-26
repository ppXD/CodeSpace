using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Per-invocation context handed to <c>INodeRuntime.RunAsync</c>. Inputs and Config arrive
/// already resolved — the engine has run <c>VariableResolver</c> over the raw NodeDefinition
/// values, so the node sees concrete values (strings, numbers, objects), never raw
/// <c>{{ref}}</c> templates.
/// </summary>
public sealed record NodeRunContext
{
    /// <summary>Resolved inputs. Already validated against the manifest's <c>InputSchema</c>.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Inputs { get; init; }

    /// <summary>Resolved static config. Already validated against the manifest's <c>ConfigSchema</c>.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Config { get; init; }

    /// <summary>
    /// Raw, un-resolved config from the NodeDefinition — {{ref}} templates still intact.
    /// Iteration-style nodes (flow.iterate) use this to re-resolve templates per iteration
    /// with a different (item/index-populated) scope. Most nodes should use <see cref="Config"/>.
    /// </summary>
    public required JsonElement RawConfig { get; init; }

    /// <summary>Raw, un-resolved inputs. Mirror of <see cref="RawConfig"/> for iteration nodes that need to re-resolve their inputs per iteration.</summary>
    public required JsonElement RawInputs { get; init; }

    /// <summary>
    /// Read-only access to the engine's run scope (trigger payload + previously-executed
    /// nodes' outputs). Most nodes won't need this — they should rely on declared
    /// <see cref="Inputs"/> instead. Exposed for the rare case where a node legitimately
    /// needs to introspect (e.g. logic.merge picking the first non-null upstream).
    /// </summary>
    public required NodeRunScope Scope { get; init; }

    /// <summary>Per-node logger. Already scoped to (workflow_run.id, node.id) — node just logs.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// Per-node observability handle. Nodes use this to wrap external calls (HTTP, LLM, Git
    /// provider APIs) and to persist large payloads as artifacts. See
    /// <see cref="INodeObservability"/> for the contract.
    /// </summary>
    public required INodeObservability Observability { get; init; }
}
