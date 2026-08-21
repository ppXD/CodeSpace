using System.Data;
using System.Data.Common;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Additive, observation-only run metadata reader. Every query is bounded and body-blind: execution payload JSON and
/// artifact references never cross this seam. The legacy full-detail and engine replay paths remain separate.
/// </summary>
public sealed class WorkflowRunViewMetadataReader : IWorkflowRunViewMetadataReader, IScopedDependency
{
    public const int MaximumLineageAttempts = WorkflowRunViewAdmissionService.MaximumLineageAttempts;
    public const int MaximumTopologyNodes = 1000;
    public const int MaximumTopologyEdges = 5000;
    // A valid map may admit 10,000 branches. Budget two complete maximum-size branch waves plus the bounded
    // top-level topology; deeper/nested valid graphs remain honest through the explicit Truncated state.
    public const int MaximumCells = Engine.MapPlan.MaxBranchesCeiling * 2 + MaximumTopologyNodes;
    public const int MaximumLinks = MaximumCells;
    public const int MaximumDefinitionJsonBytes = 8 * 1024 * 1024;
    public const int MaximumIdentityCharacters = WorkflowRunViewAdmissionService.MaximumIdentityCharacters;
    public const int MaximumLabelCharacters = 512;
    public const int MaximumConditionCharacters = 1024;

    internal const string CellMetadataSql = WorkflowRunViewAdmissionService.SelectedCellsSql;

    internal const string LinkMetadataSql = """
        SELECT wait.run_id,
               CASE WHEN char_length(wait.node_id) BETWEEN 1 AND @max_identity_chars THEN wait.node_id END AS node_id,
               CASE WHEN char_length(wait.iteration_key) <= @max_identity_chars THEN wait.iteration_key END AS iteration_key,
               wait.wait_kind,
               CASE WHEN char_length(wait.token) <= 128
                          AND wait.token ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$'
                    THEN wait.token END AS token,
               NOT (char_length(wait.node_id) BETWEEN 1 AND @max_identity_chars
                    AND char_length(wait.iteration_key) <= @max_identity_chars
                    AND char_length(wait.token) <= 128
                    AND wait.token ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$') AS identity_invalid
        FROM workflow_run_wait AS wait
        WHERE wait.run_id = ANY(@run_ids)
          AND wait.wait_kind IN ('Subworkflow', 'AgentRun')
        ORDER BY wait.run_id, wait.node_id, wait.iteration_key, wait.wait_kind
        LIMIT @take
        """;

    // PostgreSQL must detoast one frozen definition value to extract its graph, but only this narrow, hard-capped JSON
    // crosses the process boundary. Node config, prompts, inputs, workflow IO schemas and completion policy stay server-side.
    internal const string TopologySql = """
        WITH source AS (
            SELECT coalesce(run.definition_snapshot_jsonb, version.definition_jsonb) AS definition
            FROM workflow_run AS run
            LEFT JOIN workflow_version AS version
              ON version.workflow_id = run.workflow_id
             AND version.version = run.workflow_version
            WHERE run.team_id = @team_id
              AND run.id = @run_id
            LIMIT 1
        ), assessed AS (
            SELECT definition,
                   CASE
                       WHEN definition IS NULL THEN 'Unavailable'
                       WHEN octet_length(definition::text) > @max_definition_bytes THEN 'TooLarge'
                       WHEN jsonb_typeof(definition) IS DISTINCT FROM 'object'
                         OR jsonb_typeof(definition -> 'nodes') IS DISTINCT FROM 'array'
                         OR jsonb_typeof(definition -> 'edges') IS DISTINCT FROM 'array' THEN 'Corrupt'
                       WHEN jsonb_array_length(definition -> 'nodes') > @max_nodes
                         OR jsonb_array_length(definition -> 'edges') > @max_edges THEN 'TooLarge'
                       WHEN EXISTS (
                           SELECT 1 FROM jsonb_array_elements(definition -> 'nodes') AS item(node)
                           WHERE jsonb_typeof(node) IS DISTINCT FROM 'object'
                              OR jsonb_typeof(node -> 'id') IS DISTINCT FROM 'string'
                              OR char_length(node ->> 'id') NOT BETWEEN 1 AND @max_identity_chars
                              OR jsonb_typeof(node -> 'typeKey') IS DISTINCT FROM 'string'
                              OR char_length(node ->> 'typeKey') NOT BETWEEN 1 AND @max_identity_chars
                              OR (node ? 'label' AND node -> 'label' <> 'null'::jsonb
                                  AND (jsonb_typeof(node -> 'label') IS DISTINCT FROM 'string' OR char_length(node ->> 'label') > @max_label_chars))
                              OR (node ? 'parentId' AND node -> 'parentId' <> 'null'::jsonb
                                  AND (jsonb_typeof(node -> 'parentId') IS DISTINCT FROM 'string' OR char_length(node ->> 'parentId') > @max_identity_chars))
                              OR (node ? 'position' AND node -> 'position' <> 'null'::jsonb
                                  AND (jsonb_typeof(node -> 'position') IS DISTINCT FROM 'object'
                                       OR jsonb_typeof(node -> 'position' -> 'x') IS DISTINCT FROM 'number'
                                       OR jsonb_typeof(node -> 'position' -> 'y') IS DISTINCT FROM 'number'
                                       OR char_length(node -> 'position' ->> 'x') > 64
                                       OR char_length(node -> 'position' ->> 'y') > 64))
                              OR (node ? 'width' AND node -> 'width' <> 'null'::jsonb
                                  AND (jsonb_typeof(node -> 'width') IS DISTINCT FROM 'number' OR char_length(node ->> 'width') > 64))
                              OR (node ? 'height' AND node -> 'height' <> 'null'::jsonb
                                  AND (jsonb_typeof(node -> 'height') IS DISTINCT FROM 'number' OR char_length(node ->> 'height') > 64))
                       ) THEN 'Corrupt'
                       WHEN EXISTS (
                           SELECT 1 FROM jsonb_array_elements(definition -> 'edges') AS item(edge)
                           WHERE jsonb_typeof(edge) IS DISTINCT FROM 'object'
                              OR jsonb_typeof(edge -> 'from') IS DISTINCT FROM 'string'
                              OR char_length(edge ->> 'from') NOT BETWEEN 1 AND @max_identity_chars
                              OR jsonb_typeof(edge -> 'to') IS DISTINCT FROM 'string'
                              OR char_length(edge ->> 'to') NOT BETWEEN 1 AND @max_identity_chars
                              OR (edge ? 'sourceHandle' AND edge -> 'sourceHandle' <> 'null'::jsonb
                                  AND (jsonb_typeof(edge -> 'sourceHandle') IS DISTINCT FROM 'string' OR char_length(edge ->> 'sourceHandle') > @max_identity_chars))
                              OR (edge ? 'targetHandle' AND edge -> 'targetHandle' <> 'null'::jsonb
                                  AND (jsonb_typeof(edge -> 'targetHandle') IS DISTINCT FROM 'string' OR char_length(edge ->> 'targetHandle') > @max_identity_chars))
                              OR (edge ? 'condition' AND edge -> 'condition' <> 'null'::jsonb
                                  AND (jsonb_typeof(edge -> 'condition') IS DISTINCT FROM 'string' OR char_length(edge ->> 'condition') > @max_condition_chars))
                       ) THEN 'Corrupt'
                       ELSE 'Available'
                   END AS availability
            FROM source
        )
        SELECT availability,
               CASE WHEN availability = 'Available' THEN jsonb_build_object(
                   'nodes', (
                       SELECT coalesce(jsonb_agg(jsonb_strip_nulls(jsonb_build_object(
                           'id', node -> 'id',
                           'typeKey', node -> 'typeKey',
                           'label', node -> 'label',
                           'parentId', node -> 'parentId',
                           'position', CASE WHEN node -> 'position' IS NULL OR node -> 'position' = 'null'::jsonb THEN NULL
                               ELSE jsonb_build_object('x', node -> 'position' -> 'x', 'y', node -> 'position' -> 'y') END,
                           'width', node -> 'width',
                           'height', node -> 'height'
                       )) ORDER BY ordinal), '[]'::jsonb)
                       FROM jsonb_array_elements(definition -> 'nodes') WITH ORDINALITY AS item(node, ordinal)
                   ),
                   'edges', (
                       SELECT coalesce(jsonb_agg(jsonb_strip_nulls(jsonb_build_object(
                           'from', edge -> 'from',
                           'to', edge -> 'to',
                           'sourceHandle', edge -> 'sourceHandle',
                           'targetHandle', edge -> 'targetHandle',
                           'condition', edge -> 'condition'
                       )) ORDER BY ordinal), '[]'::jsonb)
                       FROM jsonb_array_elements(definition -> 'edges') WITH ORDINALITY AS item(edge, ordinal)
                   )
               )::text END AS topology_json
        FROM assessed
        """;

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement EmptyJson = JsonSerializer.SerializeToElement(new { });

    private readonly CodeSpaceDbContext _db;
    private readonly INodeRegistry _nodeRegistry;
    private readonly IWorkflowRunViewAdmission _admission;

    public WorkflowRunViewMetadataReader(CodeSpaceDbContext db, INodeRegistry nodeRegistry, IWorkflowRunViewAdmission admission)
    {
        _db = db;
        _nodeRegistry = nodeRegistry;
        _admission = admission;
    }

    public async Task<WorkflowRunViewMetadata?> ReadAsync(Guid runId, Guid teamId, WorkflowRunViewScope scope, CancellationToken cancellationToken)
    {
        var admitted = await _admission.AdmitAsync(runId, teamId, scope, cancellationToken).ConfigureAwait(false);
        if (admitted is null) return null;
        var run = admitted.Header;

        var topology = await ReadTopologyAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var cellResult = admitted.LineageAvailability == WorkflowRunViewAvailability.Available
            ? await ReadCellsAsync(admitted, topology.Topology, cancellationToken).ConfigureAwait(false)
            : new CellResult(admitted.LineageAvailability, WorkflowRunViewAvailability.Unavailable, Array.Empty<WorkflowRunCellMetadata>());

        return new WorkflowRunViewMetadata
        {
            RunId = run.Id,
            RunNumber = run.RunNumber,
            WorkflowId = run.WorkflowId,
            WorkflowVersion = run.WorkflowVersion,
            SourceType = run.SourceType,
            ParentRunId = run.ParentRunId,
            Status = run.Status,
            HasError = run.HasError,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            CreatedDate = run.CreatedDate,
            Scope = scope,
            CellsAvailability = cellResult.Availability,
            LinksAvailability = cellResult.LinksAvailability,
            Cells = cellResult.Cells,
            TopologyAvailability = topology.Availability,
            Topology = topology.Topology,
        };
    }

    private async Task<CellResult> ReadCellsAsync(WorkflowRunViewAdmission admission, WorkflowRunCanvasTopology? topology, CancellationToken cancellationToken)
    {
        var rows = (await _admission.ReadSelectedCellsAsync(admission, coordinate: null, MaximumCells + 1, cancellationToken).ConfigureAwait(false)).ToList();
        var cellsAvailability = rows.Count > MaximumCells ? WorkflowRunViewAvailability.Truncated : WorkflowRunViewAvailability.Available;
        if (rows.Count > MaximumCells) rows.RemoveAt(rows.Count - 1);
        if (rows.Any(value => value.IdentityInvalid)) return new CellResult(WorkflowRunViewAvailability.Corrupt, WorkflowRunViewAvailability.Unavailable, Array.Empty<WorkflowRunCellMetadata>());

        var selected = rows;

        var links = await ReadLinksAsync(admission.Lineage.Select(value => value.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        var linksAvailability = links.Count > MaximumLinks
            ? WorkflowRunViewAvailability.TooLarge
            : links.Any(value => value.IdentityInvalid) ? WorkflowRunViewAvailability.Corrupt : WorkflowRunViewAvailability.Available;
        var linkByCell = linksAvailability == WorkflowRunViewAvailability.Available
            ? links.ToDictionary(value => (value.RunId, value.NodeId!, value.IterationKey!, value.WaitKind), value => value.Token)
            : new Dictionary<(Guid, string, string, string), string?>();

        var containerTypeById = topology?.Nodes.ToDictionary(value => value.Id, value => value.TypeKey, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var statusByTopLevelNode = selected.Where(value => value.IterationKey == WorkflowIterationKeys.TopLevel).ToDictionary(value => value.NodeId!, value => Status(value.RecordType), StringComparer.Ordinal);
        var rerunnable = cellsAvailability == WorkflowRunViewAvailability.Available
            ? ComputeRerunnable(topology, statusByTopLevelNode)
            : new HashSet<string>();

        var cells = selected.Select(value => new WorkflowRunCellMetadata
        {
            SourceRunId = value.SourceRunId,
            NodeId = value.NodeId!,
            IterationKey = value.IterationKey!,
            ContainerKind = ResolveContainerKind(value.IterationKey!, containerTypeById),
            Status = value.Status,
            StartedAt = value.StartedAt,
            CompletedAt = value.CompletedAt,
            ChildRunId = ReadGuid(linkByCell.GetValueOrDefault((value.SourceRunId, value.NodeId!, value.IterationKey!, WorkflowWaitKinds.Subworkflow))),
            AgentRunId = ReadGuid(linkByCell.GetValueOrDefault((value.SourceRunId, value.NodeId!, value.IterationKey!, WorkflowWaitKinds.AgentRun))),
            RerunnableFromHere = value.IterationKey == WorkflowIterationKeys.TopLevel && rerunnable.Contains(value.NodeId!),
        }).ToList();

        return new CellResult(cellsAvailability, linksAvailability, cells);
    }

    private async Task<List<LinkRow>> ReadLinksAsync(Guid[] runIds, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = Command(connection, LinkMetadataSql);
            Add(command, "run_ids", runIds);
            Add(command, "take", DbType.Int32, MaximumLinks + 1);
            Add(command, "max_identity_chars", DbType.Int32, MaximumIdentityCharacters);
            var rows = new List<LinkRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new LinkRow(reader.GetGuid(0), NullableString(reader, 1), NullableString(reader, 2), reader.GetString(3), NullableString(reader, 4), reader.GetBoolean(5)));
            return rows;
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<TopologyResult> ReadTopologyAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = Command(connection, TopologySql);
            Add(command, "team_id", DbType.Guid, teamId);
            Add(command, "run_id", DbType.Guid, runId);
            Add(command, "max_nodes", DbType.Int32, MaximumTopologyNodes);
            Add(command, "max_edges", DbType.Int32, MaximumTopologyEdges);
            Add(command, "max_definition_bytes", DbType.Int32, MaximumDefinitionJsonBytes);
            Add(command, "max_identity_chars", DbType.Int32, MaximumIdentityCharacters);
            Add(command, "max_label_chars", DbType.Int32, MaximumLabelCharacters);
            Add(command, "max_condition_chars", DbType.Int32, MaximumConditionCharacters);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new TopologyResult(WorkflowRunViewAvailability.Unavailable, null);

            var availability = Enum.TryParse<WorkflowRunViewAvailability>(reader.GetString(0), out var parsed) ? parsed : WorkflowRunViewAvailability.Corrupt;
            if (availability != WorkflowRunViewAvailability.Available || reader.IsDBNull(1)) return new TopologyResult(availability, null);

            try
            {
                var topology = JsonSerializer.Deserialize<WorkflowRunCanvasTopology>(reader.GetString(1), WireJson);
                return topology is not null && IsValid(topology)
                    ? new TopologyResult(WorkflowRunViewAvailability.Available, topology)
                    : new TopologyResult(WorkflowRunViewAvailability.Corrupt, null);
            }
            catch (JsonException)
            {
                return new TopologyResult(WorkflowRunViewAvailability.Corrupt, null);
            }
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private IReadOnlySet<string> ComputeRerunnable(WorkflowRunCanvasTopology? topology, IReadOnlyDictionary<string, NodeStatus> topLevelStatusByNodeId)
    {
        if (topology is null) return new HashSet<string>();
        var definition = Definition(topology);
        var typeByNode = definition.Nodes.ToDictionary(value => value.Id, value => value.TypeKey, StringComparer.Ordinal);

        return definition.Nodes.Where(value => value.ParentId is null).Select(value => value.Id).Where(nodeId =>
        {
            RerunPlan plan;
            try { plan = RerunFromNodePlanner.Plan(definition, nodeId); }
            catch (RerunTargetNotFoundException) { return false; }

            if (plan.ReRunNodeIds.Any(id => !typeByNode.TryGetValue(id, out var typeKey) || !_nodeRegistry.Contains(typeKey)
                || !RerunDispositions.Admits(_nodeRegistry.Resolve(typeKey).Manifest, RerunContext.FromNodeRoot, id, exemptMapId: null))) return false;

            return plan.KeptNodeIds.All(keptId => !topLevelStatusByNodeId.TryGetValue(keptId, out var status)
                || status is NodeStatus.Success or NodeStatus.Skipped
                || (status == NodeStatus.Failure && definition.Edges.Any(edge => edge.From == keptId && edge.SourceHandle == WorkflowHandles.Error)));
        }).ToHashSet(StringComparer.Ordinal);
    }

    private static WorkflowDefinition Definition(WorkflowRunCanvasTopology topology) => new()
    {
        Nodes = topology.Nodes.Select(value => new NodeDefinition
        {
            Id = value.Id, TypeKey = value.TypeKey, Label = value.Label, ParentId = value.ParentId, Config = EmptyJson, Inputs = EmptyJson,
            Position = value.Position is null ? null : new NodePosition { X = value.Position.X, Y = value.Position.Y }, Width = value.Width, Height = value.Height,
        }).ToList(),
        Edges = topology.Edges.Select(value => new EdgeDefinition
        {
            From = value.From, To = value.To, SourceHandle = value.SourceHandle, TargetHandle = value.TargetHandle, Condition = value.Condition,
        }).ToList(),
    };

    private static bool IsValid(WorkflowRunCanvasTopology topology)
    {
        if (topology.Nodes.Count > MaximumTopologyNodes || topology.Edges.Count > MaximumTopologyEdges) return false;
        if (topology.Nodes.Any(value => string.IsNullOrEmpty(value.Id) || value.Id.Length > MaximumIdentityCharacters
            || string.IsNullOrEmpty(value.TypeKey) || value.TypeKey.Length > MaximumIdentityCharacters
            || value.Label?.Length > MaximumLabelCharacters || value.ParentId?.Length > MaximumIdentityCharacters
            || !Finite(value.Position?.X) || !Finite(value.Position?.Y) || !Finite(value.Width) || !Finite(value.Height))) return false;
        if (topology.Nodes.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != topology.Nodes.Count) return false;
        var ids = topology.Nodes.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        if (topology.Nodes.Any(value => value.ParentId is not null && !ids.Contains(value.ParentId))) return false;
        return topology.Edges.All(value => ids.Contains(value.From) && ids.Contains(value.To)
            && value.From.Length <= MaximumIdentityCharacters && value.To.Length <= MaximumIdentityCharacters
            && (value.SourceHandle is null || value.SourceHandle.Length <= MaximumIdentityCharacters)
            && (value.TargetHandle is null || value.TargetHandle.Length <= MaximumIdentityCharacters)
            && (value.Condition is null || value.Condition.Length <= MaximumConditionCharacters));
    }

    private static bool Finite(double? value) => value is null || (double.IsFinite(value.Value) && Math.Abs(value.Value) <= 10_000_000);

    private static NodeStatus Status(string recordType) => recordType switch
    {
        "node.started" => NodeStatus.Running,
        "node.completed" => NodeStatus.Success,
        "node.failed" => NodeStatus.Failure,
        "node.skipped" => NodeStatus.Skipped,
        "node.suspended" => NodeStatus.Suspended,
        _ => NodeStatus.Pending,
    };

    private static string? ResolveContainerKind(string iterationKey, IReadOnlyDictionary<string, string> typeByNodeId)
    {
        if (iterationKey.Length == 0) return null;
        var segment = iterationKey[(iterationKey.LastIndexOf('/') + 1)..];
        var hash = segment.LastIndexOf('#');
        return hash <= 0 ? null : typeByNodeId.GetValueOrDefault(segment[..hash]);
    }

    private static Guid? ReadGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;
    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableInstant(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record LinkRow(Guid RunId, string? NodeId, string? IterationKey, string WaitKind, string? Token, bool IdentityInvalid);
    private sealed record CellResult(WorkflowRunViewAvailability Availability, WorkflowRunViewAvailability LinksAvailability, IReadOnlyList<WorkflowRunCellMetadata> Cells);
    private sealed record TopologyResult(WorkflowRunViewAvailability Availability, WorkflowRunCanvasTopology? Topology);
}
