using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Failures;

/// <summary>
/// Reads the provider failure one cell recorded from the two places a node's failure is written — the cell's own <c>node.failed</c>
/// record, then the agent run that worked in it — in the order, and with the parsers, <see cref="RunFailureReader"/> uses for the
/// node that ended a run. So a failure inside a container reads exactly as it would have at the top level of a run. Never throws on a
/// record it cannot read: an old row, or one written before failures were kept, simply has none.
/// </summary>
public sealed class CellFailureReader : ICellFailureReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public CellFailureReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<PersistedFailure?> ReadAsync(Guid runId, Guid teamId, string nodeId, string iterationKey, CancellationToken cancellationToken)
    {
        var own = await ReadOwnRecordAsync(runId, nodeId, iterationKey, cancellationToken).ConfigureAwait(false);

        return own ?? await ReadAgentRunAsync(runId, teamId, nodeId, iterationKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The newest <c>node.failed</c> record of the cell: a node that failed on one walk and again on the next says why it failed last.</summary>
    private async Task<PersistedFailure?> ReadOwnRecordAsync(Guid runId, string nodeId, string iterationKey, CancellationToken cancellationToken)
    {
        var payload = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed && r.NodeId == nodeId && r.IterationKey == iterationKey)
            .OrderByDescending(r => r.Sequence)
            .Select(r => r.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return PersistedFailureJson.Read(payload);
    }

    /// <summary>The last agent run of the cell only: an earlier attempt that met a disconnected credential and was respawned says nothing about why the cell ended.</summary>
    private async Task<PersistedFailure?> ReadAgentRunAsync(Guid runId, Guid teamId, string nodeId, string iterationKey, CancellationToken cancellationToken)
    {
        var result = await _db.AgentRun.AsNoTracking()
            .Where(a => a.WorkflowRunId == runId && a.TeamId == teamId && a.NodeId == nodeId && a.IterationKey == iterationKey && a.ResultJson != null)
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => a.ResultJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return result is null ? null : RunFailureReader.ReadAgentResult(result);
    }
}
