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
                await ReEmitAsync(newRunId, cell, originalRunId, cancellationToken).ConfigureAwait(false);
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

    private async Task ReEmitAsync(Guid newRunId, WorkflowRunNode cell, Guid originalRunId, CancellationToken cancellationToken)
    {
        switch (cell.Status)
        {
            case NodeStatus.Success:
                // routingHints MUST carry over — null hints make IsEdgeLive treat every handle as live, so a
                // dead branch the original routed around would wrongly re-enliven on the fork.
                await _recordLogger.NodeCompletedAsync(newRunId, cell.NodeId, WorkflowIterationKeys.TopLevel, ParseOutputs(cell.OutputsJson), ParseHints(cell.RoutingHintsJson), TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                break;

            case NodeStatus.Skipped:
                await _recordLogger.NodeSkippedAsync(newRunId, cell.NodeId, WorkflowIterationKeys.TopLevel, $"Reused from run {originalRunId}.", cancellationToken).ConfigureAwait(false);
                break;

            case NodeStatus.Failure:
                // A kept Failure cell is always error-edge-handled (the service refuses an un-handled failed
                // upstream); RehydrateFromLedger re-settles it as a failed source + rebuilds the error output.
                await _recordLogger.NodeFailedAsync(newRunId, cell.NodeId, WorkflowIterationKeys.TopLevel, cell.Error ?? "(failed)", TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // Defence-in-depth: ResolveReusableKeptCellsAsync only ever passes Success / Skipped /
                // error-edge-handled Failure cells. A non-terminal status reaching here is a contract breach —
                // fail loudly rather than silently drop a kept cell (which would orphan the fork's frontier).
                throw new InvalidOperationException(
                    $"Cannot re-emit kept cell '{cell.NodeId}' from run {originalRunId}: unexpected non-terminal status {cell.Status}.");
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
