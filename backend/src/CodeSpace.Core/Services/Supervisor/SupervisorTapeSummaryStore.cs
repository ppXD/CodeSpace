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
        var row = await _db.SupervisorTapeSummaryRecord
            .SingleOrDefaultAsync(r => r.SupervisorRunId == supervisorRunId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (row == null)
        {
            _db.SupervisorTapeSummaryRecord.Add(new SupervisorTapeSummaryRecord
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                SupervisorRunId = supervisorRunId,
                UpToSequence = upToSequence,
                Summary = summary,
                CreatedDate = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            // Forward-only: a stale writer's lower (or equal) sequence is ignored — the newer digest already covers it.
            if (upToSequence <= row.UpToSequence) return;

            row.UpToSequence = upToSequence;
            row.Summary = summary;
            row.UpdatedDate = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
