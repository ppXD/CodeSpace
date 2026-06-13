using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Loads a team's agent runs (optionally windowed / harness-filtered), projects each to an
/// <see cref="AgentRunOutcome"/>, and hands them to the pure <see cref="EvalScorecard"/>. Thin (Rule 16) — the
/// service owns only the team-scoped query + projection; all the scoring math is the pure scorer's.
/// </summary>
public sealed class AgentRunScorecardService : IAgentRunScorecardService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public AgentRunScorecardService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<AgentRunScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, string? harness, CancellationToken cancellationToken)
    {
        var query = _db.AgentRun.AsNoTracking().Where(r => r.TeamId == teamId);

        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        if (!string.IsNullOrWhiteSpace(harness)) query = query.Where(r => r.Harness == harness);

        var rows = await query
            .Select(r => new { r.Harness, r.Status, r.StartedAt, r.CompletedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var outcomes = rows
            .Select(r => new AgentRunOutcome
            {
                Harness = r.Harness,
                Status = r.Status,
                DurationSeconds = r.StartedAt is { } started && r.CompletedAt is { } completed ? (completed - started).TotalSeconds : null,
            })
            .ToList();

        return EvalScorecard.Compute(outcomes);
    }
}
