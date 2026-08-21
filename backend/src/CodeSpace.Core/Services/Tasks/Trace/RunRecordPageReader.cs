using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// Keyset reader over idx_wrr_run_sequence. Every body-bearing query takes at most MaxLimit+1 rows and never counts or
/// offsets the ledger. The separate team-scoped status projection preserves 404 conflation even for a run with no
/// records, without loading the WorkflowRun entity or its JSON bodies.
/// </summary>
public sealed class RunRecordPageReader : IRunRecordPageReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public RunRecordPageReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<RunRecordPageResponse?> ReadAsync(RunRecordPageRequest request, CancellationToken cancellationToken)
    {
        request.Validate();

        var status = await RunStatusQuery(_db, request.RunId, request.TeamId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (status == null) return null;

        var take = request.Limit + 1;
        var mode = Mode(request);
        var rows = mode switch
        {
            RunRecordPageModes.Newer => await NewerRowsQuery(_db, request.RunId, request.AfterSequence!.Value, take).ToListAsync(cancellationToken).ConfigureAwait(false),
            RunRecordPageModes.Older => await OlderRowsQuery(_db, request.RunId, request.BeforeSequence!.Value, take).ToListAsync(cancellationToken).ConfigureAwait(false),
            _ => await TailRowsQuery(_db, request.RunId, take).ToListAsync(cancellationToken).ConfigureAwait(false),
        };

        var hasMore = rows.Count > request.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        if (mode != RunRecordPageModes.Newer) rows.Reverse();

        return new RunRecordPageResponse
        {
            RunId = request.RunId,
            RunStatus = status.Value.ToString(),
            Mode = mode,
            Records = rows,
            NextBeforeSequence = mode != RunRecordPageModes.Newer && hasMore ? rows[0].Sequence : null,
            NextAfterSequence = mode == RunRecordPageModes.Newer && hasMore ? rows[^1].Sequence : null,
        };
    }

    internal static IQueryable<WorkflowRunStatus?> RunStatusQuery(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        db.WorkflowRun.AsNoTracking().Where(run => run.Id == runId && run.TeamId == teamId).Select(run => (WorkflowRunStatus?)run.Status);

    internal static IQueryable<RunRecordView> TailRowsQuery(CodeSpaceDbContext db, Guid runId, int take) =>
        Project(db.WorkflowRunRecord.AsNoTracking().Where(row => row.RunId == runId).OrderByDescending(row => row.Sequence).Take(take));

    internal static IQueryable<RunRecordView> OlderRowsQuery(CodeSpaceDbContext db, Guid runId, long beforeSequence, int take) =>
        Project(db.WorkflowRunRecord.AsNoTracking().Where(row => row.RunId == runId && row.Sequence < beforeSequence).OrderByDescending(row => row.Sequence).Take(take));

    internal static IQueryable<RunRecordView> NewerRowsQuery(CodeSpaceDbContext db, Guid runId, long afterSequence, int take) =>
        Project(db.WorkflowRunRecord.AsNoTracking().Where(row => row.RunId == runId && row.Sequence > afterSequence).OrderBy(row => row.Sequence).Take(take));

    private static IQueryable<RunRecordView> Project(IQueryable<WorkflowRunRecord> query) => query.Select(row => new RunRecordView
    {
        Sequence = row.Sequence,
        RecordType = row.RecordType,
        NodeId = row.NodeId,
        IterationKey = row.IterationKey,
        OccurredAt = row.OccurredAt,
        PayloadJson = row.PayloadJson,
        CorrelationId = row.CorrelationId,
        ParentRecordId = row.ParentRecordId,
    });

    private static string Mode(RunRecordPageRequest request) => request.AfterSequence.HasValue
        ? RunRecordPageModes.Newer
        : request.BeforeSequence.HasValue ? RunRecordPageModes.Older : RunRecordPageModes.Tail;
}
