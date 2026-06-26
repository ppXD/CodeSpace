using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Rerun;

/// <summary>
/// Pre-seeds a from-node rerun's KEPT (reused) cells onto the new forked run so the engine's
/// <c>RehydrateFromLedger</c> settles them and the frontier resumes at the chosen node (D7). Cross-run cell
/// cloning is its own concern (Rule 7/16/18) — distinct from the per-event <see cref="IRunRecordLogger"/> the
/// engine uses while a run executes — so it lives here, not as a new method on the engine-facing logger.
///
/// <para>Re-emits each kept top-level cell through the SANCTIONED <see cref="IRunRecordLogger"/>
/// (<c>NodeCompleted</c>/<c>NodeSkipped</c>/<c>NodeFailed</c>) so the pre-seeded cell projects through the
/// <c>workflow_run_node</c> view byte-identically to a real one and round-trips the same parse
/// <c>RehydrateFromLedger</c> expects. Emits ONLY the terminal record (no <c>node.started</c>) — so a reused
/// node shows zero starts on the fork (the reuse-vs-rerun discriminator) and carries no stale timestamp — and
/// ONLY top-level cells (a kept container is one settled leaf; the engine never re-enters its body).</para>
/// </summary>
public interface IRerunCellSeeder
{
    /// <summary>
    /// Re-emit the original run's settled top-level cells named by <paramref name="keptNodeIds"/> onto
    /// <paramref name="newRunId"/>. When <paramref name="writeEmptySnapshotSentinel"/> is true (the original
    /// carried no variable snapshot) also writes a sentinel variable row so the engine takes the REPLAY (frozen)
    /// scope path rather than the fresh path. Pure INSERTs onto the new run — never touches the original.
    /// </summary>
    Task SeedKeptCellsAsync(Guid originalRunId, Guid newRunId, IReadOnlySet<string> keptNodeIds, bool writeEmptySnapshotSentinel, CancellationToken cancellationToken);

    /// <summary>
    /// Pre-seed the REUSED sibling branches of a map-branch rerun (D7-4). For a top-level map <paramref name="mapNodeId"/>
    /// fanned out over <paramref name="branchCount"/> elements, re-emits every NON-target branch's cells faithfully
    /// (each row's real NodeId + status + outputs, under the branch key <c>"&lt;mapId&gt;#&lt;j&gt;"</c>) so the engine's
    /// <c>RehydrateMapResults</c>/<c>TrySettleBranch</c> REPLAYS those siblings (no side-effect re-fire) on the fork.
    /// The TARGET branches (<paramref name="rerunIndices"/>) are OMITTED — with no rows under their keys, the map re-runs
    /// them fresh. The map's OWN top-level cell is NOT seeded here (the planner keeps it in the re-run set), so the map
    /// re-enters and re-aggregates. Branch cells are terminal-record-only (no node.started) — the reuse discriminator.
    /// The single-branch rerun is the <c>|rerunIndices| == 1</c> case of this same set primitive.
    /// </summary>
    Task SeedSiblingBranchCellsAsync(Guid originalRunId, Guid newRunId, string mapNodeId, IReadOnlySet<int> rerunIndices, int branchCount, CancellationToken cancellationToken);
}

public sealed class RerunCellSeeder : IRerunCellSeeder, IScopedDependency
{
    // A scope the engine's snapshot scope-build ignores (it reads only "Workflow"/"Team" rows) — so the
    // sentinel forces isReplay=true without injecting a phantom variable into wf/team scope.
    private const string SentinelScope = "Sys";

    private readonly CodeSpaceDbContext _db;
    private readonly IRunRecordLogger _recordLogger;

    public RerunCellSeeder(CodeSpaceDbContext db, IRunRecordLogger recordLogger)
    {
        _db = db;
        _recordLogger = recordLogger;
    }

    public async Task SeedKeptCellsAsync(Guid originalRunId, Guid newRunId, IReadOnlySet<string> keptNodeIds, bool writeEmptySnapshotSentinel, CancellationToken cancellationToken)
    {
        if (keptNodeIds.Count > 0)
        {
            var cells = await _db.WorkflowRunNode.AsNoTracking()
                .Where(n => n.RunId == originalRunId && n.IterationKey == WorkflowIterationKeys.TopLevel && keptNodeIds.Contains(n.NodeId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var cell in cells)
                await ReEmitAsync(newRunId, cell, WorkflowIterationKeys.TopLevel, originalRunId, cancellationToken).ConfigureAwait(false);
        }

        if (writeEmptySnapshotSentinel)
            _db.WorkflowRunVariable.Add(new WorkflowRunVariable
            {
                Id = Guid.NewGuid(),
                RunId = newRunId,
                Scope = SentinelScope,
                Name = "__rerun__",
                ValueType = nameof(VariableValueType.String),
                ValuePlain = "1",
                CapturedAt = DateTimeOffset.UtcNow,
            });
    }

    public async Task SeedSiblingBranchCellsAsync(Guid originalRunId, Guid newRunId, string mapNodeId, IReadOnlySet<int> rerunIndices, int branchCount, CancellationToken cancellationToken)
    {
        // A top-level map's branch key is exactly "<mapId>#<i>" (CombineIterationKey("", seg) == seg). Load the
        // original's branch-scoped rows, group by EXACT key, and re-emit each NON-target branch faithfully.
        var prefix = $"{mapNodeId}#";
        var rows = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == originalRunId && n.IterationKey.StartsWith(prefix))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var byKey = rows.GroupBy(r => r.IterationKey).ToDictionary(g => g.Key, g => g.ToList());

        for (var j = 0; j < branchCount; j++)
        {
            if (rerunIndices.Contains(j)) continue;   // OMIT the re-run branches → no rows under their keys → the map re-runs them fresh

            var branchKey = $"{mapNodeId}#{j}";
            if (!byKey.TryGetValue(branchKey, out var branchRows)) continue;   // sibling never produced direct cells — nothing to replay

            // Re-emit EVERY row this sibling settled (the terminal AND any error-edge / continue-abandon body row),
            // carrying each row's REAL NodeId — TrySettleBranch reads the terminal by id (and, continue mode, the
            // abandon body node by id), so faithful per-row reproduction settles the sibling exactly as the original.
            foreach (var cell in branchRows)
                await ReEmitAsync(newRunId, cell, branchKey, originalRunId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReEmitAsync(Guid newRunId, WorkflowRunNode cell, string iterationKey, Guid originalRunId, CancellationToken cancellationToken)
    {
        switch (cell.Status)
        {
            case NodeStatus.Success:
                // routingHints MUST carry over — null hints make IsEdgeLive treat every handle as live, so a
                // dead branch the original routed around would wrongly re-enliven on the fork.
                await _recordLogger.NodeCompletedAsync(newRunId, cell.NodeId, iterationKey, ParseOutputs(cell.OutputsJson), ParseHints(cell.RoutingHintsJson), TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                break;

            case NodeStatus.Skipped:
                await _recordLogger.NodeSkippedAsync(newRunId, cell.NodeId, iterationKey, $"Reused from run {originalRunId}.", cancellationToken).ConfigureAwait(false);
                break;

            case NodeStatus.Failure:
                // A re-emitted Failure cell is one of: a top-level kept failure (error-edge-handled); a map sibling's
                // continue-mode abandon row; or a map sibling's TERMINATE-mode failed row (both: a failed body node
                // with no error edge) — all round-trip faithfully: RehydrateFromLedger / TrySettleBranch re-settle
                // each from the node.failed record (the terminate arm replays it as a preserved failure, no re-run).
                await _recordLogger.NodeFailedAsync(newRunId, cell.NodeId, iterationKey, cell.Error ?? "(failed)", TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // Defence-in-depth: callers only ever pass settled (Success / Skipped / Failure) cells. A
                // non-terminal status reaching here is a contract breach — fail loudly rather than silently
                // drop a reused cell (which would orphan the fork's frontier or mis-settle a sibling).
                throw new InvalidOperationException(
                    $"Cannot re-emit reused cell '{cell.NodeId}' (iteration '{iterationKey}') from run {originalRunId}: unexpected non-terminal status {cell.Status}.");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseOutputs(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    private static IReadOnlyList<string>? ParseHints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { return null; }
    }
}
