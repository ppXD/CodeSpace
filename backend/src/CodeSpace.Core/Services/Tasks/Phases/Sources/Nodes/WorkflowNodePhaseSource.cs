using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;

/// <summary>
/// The STRUCTURAL phase source — it reuses <see cref="IWorkflowService.GetRunAsync"/> (team-scoped) wholesale and
/// projects ONE <see cref="RunPhase"/> per TOP-LEVEL node (a row with an empty <see cref="WorkflowRunNodeSummary.IterationKey"/> —
/// not a container-internal branch). Map fan-outs roll up: a top-level node whose DIRECT branch rows carry
/// <c>ContainerKind == "flow.map"</c> and an <c>IterationKey</c> prefixed <c>"&lt;mapNodeId&gt;#"</c> becomes a single
/// 'map' phase whose Agents are those branches' agent refs + a count/failed metric from the map node's output roll-up.
/// A plain top-level node carrying an <c>AgentRunId</c> (agent.code / agent.supervisor) becomes an 'agent' phase with
/// one ref; everything else is a plain 'node' phase. The node→agentRunId + branch ContainerKind are ALREADY resolved
/// on the summary rows (PR1-PR6 substrate), so this never re-queries waits.
///
/// <para>Each <see cref="PhaseAgentRef"/> carries the GROUND-TRUTH <c>AgentRunStatus</c> name (read team-scoped from
/// the <c>AgentRun</c> rows in ONE batch query, mirroring <c>SupervisorScorecardService</c>'s id-set fold), exactly
/// like the supervisor source — never the structural <c>NodeStatus</c> name. A ref whose agent row is missing
/// (team-foreign, or not yet created) falls back to the owning node's status name. READ-ONLY.</para>
/// </summary>
public sealed class WorkflowNodePhaseSource : IRunPhaseSource, IScopedDependency
{
    public const string Key = "node-summary";

    private const string MapContainerKind = "flow.map";

    private readonly IWorkflowService _workflows;
    private readonly AgentMetricsReader _metrics;

    public WorkflowNodePhaseSource(IWorkflowService workflows, AgentMetricsReader metrics)
    {
        _workflows = workflows;
        _metrics = metrics;
    }

    public string SourceKey => Key;

    public async Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<RunPhase>();

        // ONE team-scoped read of the real AgentRun rows + tool ledger gives BOTH the ground-truth status AND the
        // per-agent metrics (duration / tokens / tool count / model), so a plain agent.code / map agent now carries the
        // SAME rollup the supervisor source folds from its ledger — not just status.
        var metricsById = await _metrics.ReadAsync(context.TeamId, AgentRunIdsOf(run.Nodes), DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        var agentStatusById = metricsById.ToDictionary(kv => kv.Key, kv => kv.Value.Status);

        return ProjectNodes(run.Nodes, agentStatusById, metricsById);
    }

    /// <summary>The distinct agent-run ids referenced by the node rows (a node carrying a parseable <c>AgentRunId</c>) — the id set the metrics read folds over.</summary>
    private static IReadOnlyList<Guid> AgentRunIdsOf(IReadOnlyList<WorkflowRunNodeSummary> nodes) =>
        nodes
            .Where(n => !string.IsNullOrEmpty(n.AgentRunId) && Guid.TryParse(n.AgentRunId, out _))
            .Select(n => Guid.Parse(n.AgentRunId!))
            .Distinct()
            .ToList();

    /// <summary>The pure projection step — node summaries + the already-resolved ground-truth agent statuses (and the optional per-agent metrics) → phases. Separated from the DB read so it is unit-testable without a DbContext. <paramref name="metricsById"/> omitted leaves the refs' duration/tokens/tool/model fields null (today's behavior).</summary>
    public static IReadOnlyList<RunPhase> ProjectNodes(IReadOnlyList<WorkflowRunNodeSummary> nodes, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById, IReadOnlyDictionary<Guid, AgentRunMetrics>? metricsById = null)
    {
        var topLevel = OrderTopLevel(nodes);
        var metrics = metricsById ?? EmptyMetrics;

        return topLevel.Select((node, index) => ToPhase(node, index, nodes, agentStatusById, metrics)).ToList();
    }

    private static IReadOnlyList<WorkflowRunNodeSummary> OrderTopLevel(IReadOnlyList<WorkflowRunNodeSummary> nodes) =>
        nodes
            .Where(n => string.IsNullOrEmpty(n.IterationKey))
            .OrderBy(n => n.StartedAt ?? DateTimeOffset.MaxValue)
            .ToList();

    private static RunPhase ToPhase(WorkflowRunNodeSummary node, int order, IReadOnlyList<WorkflowRunNodeSummary> allRows, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById, IReadOnlyDictionary<Guid, AgentRunMetrics> metricsById)
    {
        var branches = MapBranchesOf(node.NodeId, allRows);

        if (branches.Count > 0) return MapPhase(node, order, branches, agentStatusById, metricsById);

        if (!string.IsNullOrEmpty(node.AgentRunId)) return AgentPhase(node, order, agentStatusById, metricsById);

        return PlainPhase(node, order);
    }

    /// <summary>The map element-branch rows belonging DIRECTLY to this node: a branch carries ContainerKind=="flow.map" and an iteration key whose remainder after the "&lt;nodeId&gt;#" prefix has NO '/' — i.e. the engine's "&lt;mapId&gt;#&lt;i&gt;" shape, excluding a nested grandchild like "&lt;mapId&gt;#&lt;i&gt;/&lt;innerKey&gt;" (the engine composes nested keys as "&lt;outerKey&gt;/&lt;segment&gt;"). So the fan-out folds ONLY its own direct elements, matching the outer map's count/failed metric.</summary>
    private static IReadOnlyList<WorkflowRunNodeSummary> MapBranchesOf(string nodeId, IReadOnlyList<WorkflowRunNodeSummary> allRows)
    {
        var prefix = nodeId + "#";

        return allRows
            .Where(r => r.ContainerKind == MapContainerKind && IsDirectBranch(r.IterationKey, prefix))
            .OrderBy(r => r.IterationKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A direct branch of the map: the key starts with "&lt;nodeId&gt;#" and the remainder after that prefix carries NO '/' (a '/' marks a nested-container segment, i.e. a grandchild — not this map's direct element).</summary>
    private static bool IsDirectBranch(string iterationKey, string prefix) =>
        iterationKey.StartsWith(prefix, StringComparison.Ordinal) &&
        iterationKey.AsSpan(prefix.Length).IndexOf('/') < 0;

    private static RunPhase MapPhase(WorkflowRunNodeSummary node, int order, IReadOnlyList<WorkflowRunNodeSummary> branches, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById, IReadOnlyDictionary<Guid, AgentRunMetrics> metricsById)
    {
        var agents = branches.Where(b => !string.IsNullOrEmpty(b.AgentRunId)).Select(b => ToAgentRef(b, agentStatusById, metricsById)).ToList();

        return BasePhase(node, order, kind: "map", label: "Fan out", agents) with { Metrics = MapMetrics(node, agents) };
    }

    private static RunPhase AgentPhase(WorkflowRunNodeSummary node, int order, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById, IReadOnlyDictionary<Guid, AgentRunMetrics> metricsById)
    {
        var agents = new[] { ToAgentRef(node, agentStatusById, metricsById) };

        return BasePhase(node, order, kind: "agent", label: node.NodeId, agents) with
        {
            Metrics = PhaseAgentMetrics.From(agents),   // succeeded/failed from the agent's ground-truth status, not just a count
        };
    }

    private static RunPhase PlainPhase(WorkflowRunNodeSummary node, int order) =>
        BasePhase(node, order, kind: "node", label: node.NodeId, Array.Empty<PhaseAgentRef>());

    private static RunPhase BasePhase(WorkflowRunNodeSummary node, int order, string kind, string label, IReadOnlyList<PhaseAgentRef> agents) => new()
    {
        Id = node.NodeId,
        Label = label,
        Kind = kind,
        Status = PhaseStatusMap.FromNode(node.Status),
        Order = order,
        Agents = agents,
        SourceKey = Key,
        Summary = node.Error,
        StartedAt = node.StartedAt,
        CompletedAt = node.CompletedAt,
    };

    /// <summary>The agent ref for a node row, carrying the GROUND-TRUTH AgentRunStatus name + the per-agent metrics (duration / tokens / tool count / model) read team-scoped — falling back to the owning node's status name, and leaving the metric fields null, only when the agent row is missing (team-foreign or not yet created).</summary>
    private static PhaseAgentRef ToAgentRef(WorkflowRunNodeSummary node, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById, IReadOnlyDictionary<Guid, AgentRunMetrics> metricsById)
    {
        var agentRunId = Guid.Parse(node.AgentRunId!);
        var metrics = metricsById.GetValueOrDefault(agentRunId);

        return new()
        {
            AgentRunId = agentRunId,
            NodeId = node.NodeId,
            IterationKey = string.IsNullOrEmpty(node.IterationKey) ? null : node.IterationKey,
            Status = agentStatusById.TryGetValue(agentRunId, out var status) ? status.ToString() : node.Status.ToString(),
            Model = metrics?.Model,
            InputTokens = metrics?.InputTokens,
            OutputTokens = metrics?.OutputTokens,
            DurationMs = metrics?.DurationMs,
            ToolCount = metrics?.ToolCount,
            CostUsd = metrics?.CostUsd,
            FilesChanged = metrics?.FilesChanged,
        };
    }

    /// <summary>Map metrics: agent count + the engine's own count/failed roll-up off the map node's OutputsJson (best-effort — absent keys read 0).</summary>
    private static PhaseMetrics MapMetrics(WorkflowRunNodeSummary node, IReadOnlyList<PhaseAgentRef> agents) => new()
    {
        AgentCount = agents.Count,
        FailedCount = ReadIntOutput(node.Outputs, WorkflowOutputKeys.MapFailed),
        SucceededCount = Math.Max(0, ReadIntOutput(node.Outputs, WorkflowOutputKeys.MapCount) - ReadIntOutput(node.Outputs, WorkflowOutputKeys.MapFailed)),
    };

    private static int ReadIntOutput(JsonElement outputs, string key) =>
        outputs.ValueKind == JsonValueKind.Object && outputs.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static readonly IReadOnlyDictionary<Guid, AgentRunMetrics> EmptyMetrics = new Dictionary<Guid, AgentRunMetrics>();
}
