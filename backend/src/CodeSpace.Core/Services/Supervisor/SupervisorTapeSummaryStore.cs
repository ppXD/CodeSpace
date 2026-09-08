using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

public sealed class SupervisorTapeSummaryStore : ISupervisorTapeSummaryStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public SupervisorTapeSummaryStore(CodeSpaceDbContext db) { _db = db; }

    public async Task<SupervisorTapeSummary?> GetAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var row = await _db.SupervisorTapeSummaryRecord.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SupervisorRunId == supervisorRunId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        return row == null ? null : new SupervisorTapeSummary { UpToSequence = row.UpToSequence, Text = row.Summary };
    }

    public async Task UpsertAsync(Guid supervisorRunId, Guid teamId, long upToSequence, string summary, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // The sequence comparison belongs inside the same database statement as the write. An application-side read
        // lets two workers both observe N, then a slow N+2 writer overwrite a committed N+5 digest. The conflict row
        // lock serializes them and the WHERE re-evaluates against the committed winner. Team equality prevents a
        // forged cross-team call from mutating the globally unique run row.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO supervisor_tape_summary (id, team_id, supervisor_run_id, up_to_sequence, summary, created_date)
            VALUES ({id}, {teamId}, {supervisorRunId}, {upToSequence}, {summary}, {now})
            ON CONFLICT (supervisor_run_id) DO UPDATE SET
                up_to_sequence = EXCLUDED.up_to_sequence,
                summary = EXCLUDED.summary,
                updated_date = {now}
            WHERE supervisor_tape_summary.team_id = EXCLUDED.team_id
              AND supervisor_tape_summary.up_to_sequence < EXCLUDED.up_to_sequence
            """, cancellationToken).ConfigureAwait(false);
    }
}
