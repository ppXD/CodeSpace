using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<RunNodeOutputInflater> _logger;

    public RunNodeOutputInflater(IArtifactStore store, ILogger<RunNodeOutputInflater> logger)
    {
        _store = store;
        _logger = logger;
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
            nodes.Add(NeedsInflation(node, nodeIds) ? await InflateNodeAsync(node, teamId, cancellationToken).ConfigureAwait(false) : node);

        return run with { Nodes = nodes };
    }

    /// <summary>
    /// One cell's inflation, isolated. The run-detail read is the whole record of what happened, so a cell whose bytes
    /// the storage plane could not produce costs the reader THAT cell and never the other thirty-nine. The boundary is
    /// kept here rather than borrowed from <see cref="NodeOutputArtifacts.ResolveAsync"/>'s per-property shed, so no
    /// future change to what that path sheds can quietly turn one rotted output back into a failed run-detail read.
    ///
    /// <para>Unreachable while that per-property shed holds — which is exactly why it neither swallows the fact nor
    /// returns the untouched pointers: a boundary nothing crosses today is the one whose silence nobody would notice.
    /// The cell is shed onto the same reason-carrying marker one property's shed writes, and the lane is logged. The
    /// everyday signal comes from that per-property shed's own warning, not from here — a log kept only at a boundary
    /// nothing crosses is a log that never fires.</para>
    /// </summary>
    private async Task<WorkflowRunNodeSummary> InflateNodeAsync(WorkflowRunNodeSummary node, Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            return node with { Outputs = await InflateOutputsAsync(node.Outputs, teamId, cancellationToken).ConfigureAwait(false) };
        }
        catch (ArtifactContentUnavailableException ex)
        {
            _logger.LogWarning(ex, "Run detail: node {NodeId}'s offloaded outputs could not be read ({ArtifactFailureKind}); the cell keeps its pointers with that reason and the rest of the run is unaffected", node.NodeId, ex.Kind);

            return node with { Outputs = ShedOutputs(node.Outputs, ex.Kind) };
        }
    }

    /// <summary>The whole cell's references, shed the way one property's are — so the reader is told which lane failed rather than handed the bare pointers back.</summary>
    private static JsonElement ShedOutputs(JsonElement outputs, ArtifactContentUnavailableKind reason) =>
        JsonSerializer.SerializeToElement(NodeOutputArtifacts.ShedAll(PropertiesOf(outputs), reason));

    /// <summary>A cell is worth a fetch only when the caller asked for it AND its outputs really do carry an offloaded ref — so a caller that reads one node pays for one node, and a run with no offload pays nothing.</summary>
    private static bool NeedsInflation(WorkflowRunNodeSummary node, IReadOnlySet<string>? nodeIds) =>
        (nodeIds is null || nodeIds.Contains(node.NodeId)) && CarriesRef(node.Outputs);

    private static bool CarriesRef(JsonElement outputs) =>
        outputs.ValueKind == JsonValueKind.Object && outputs.EnumerateObject().Any(property => NodeOutputArtifacts.IsRef(property.Value));

    private async Task<JsonElement> InflateOutputsAsync(JsonElement outputs, Guid teamId, CancellationToken cancellationToken)
    {
        var resolved = await NodeOutputArtifacts.ResolveAsync(_store, _logger, teamId, PropertiesOf(outputs), cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(resolved);
    }

    private static Dictionary<string, JsonElement> PropertiesOf(JsonElement outputs) =>
        outputs.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
}
