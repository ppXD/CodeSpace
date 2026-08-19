using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The <see cref="IRunNodeOutputInflater"/> implementation. Fetches nothing it doesn't have to: a cell is fetched only
/// when it is in the caller's scope AND its outputs actually carry a ref, and a run with no offloaded outputs at all is
/// returned as-is (same instance, no rewrite). Every fetch goes through <see cref="NodeOutputArtifacts.ResolveAsync"/>,
/// so the ref shape, the store's read-back verification and the missing-artifact fail-safe are the shared ones.
/// </summary>
public sealed class RunNodeOutputInflater : IRunNodeOutputInflater, IScopedDependency
{
    private readonly IArtifactStore _store;

    public RunNodeOutputInflater(IArtifactStore store)
    {
        _store = store;
    }

    public Task<WorkflowRunDetail> InflateAsync(WorkflowRunDetail run, Guid teamId, CancellationToken cancellationToken) =>
        InflateScopeAsync(run, teamId, nodeIds: null, cancellationToken);

    public Task<WorkflowRunDetail> InflateAsync(WorkflowRunDetail run, Guid teamId, IReadOnlySet<string> nodeIds, CancellationToken cancellationToken) =>
        InflateScopeAsync(run, teamId, nodeIds, cancellationToken);

    private async Task<WorkflowRunDetail> InflateScopeAsync(WorkflowRunDetail run, Guid teamId, IReadOnlySet<string>? nodeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (!run.Nodes.Any(node => NeedsInflation(node, nodeIds))) return run;

        var nodes = new List<WorkflowRunNodeSummary>(run.Nodes.Count);

        foreach (var node in run.Nodes)
            nodes.Add(NeedsInflation(node, nodeIds)
                ? node with { Outputs = await InflateOutputsAsync(node.Outputs, teamId, cancellationToken).ConfigureAwait(false) }
                : node);

        return run with { Nodes = nodes };
    }

    /// <summary>A cell is worth a fetch only when the caller asked for it AND its outputs really do carry an offloaded ref — so a caller that reads one node pays for one node, and a run with no offload pays nothing.</summary>
    private static bool NeedsInflation(WorkflowRunNodeSummary node, IReadOnlySet<string>? nodeIds) =>
        (nodeIds is null || nodeIds.Contains(node.NodeId)) && CarriesRef(node.Outputs);

    private static bool CarriesRef(JsonElement outputs) =>
        outputs.ValueKind == JsonValueKind.Object && outputs.EnumerateObject().Any(property => NodeOutputArtifacts.IsRef(property.Value));

    private async Task<JsonElement> InflateOutputsAsync(JsonElement outputs, Guid teamId, CancellationToken cancellationToken)
    {
        var properties = outputs.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        var resolved = await NodeOutputArtifacts.ResolveAsync(_store, teamId, properties, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(resolved);
    }
}
