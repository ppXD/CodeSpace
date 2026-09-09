using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Learning;

public interface ILessonQualifier
{
    /// <summary>Reconciles bounded exact exposure evidence into candidate/rule qualification state. Returns changed lessons.</summary>
    Task<int> QualifyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Turns failure-derived candidates into formal rules only after independent runtime evidence. The model never
/// writes qualification: the server joins immutable prompt exposure receipts to the existing north-star scorecard.
/// A negative result is correlation rather than a causal claim, so it demotes a rule without deleting its evidence.
/// </summary>
public sealed class LessonQualifier : ILessonQualifier, IScopedDependency
{
    public const int MinimumSuccessfulExposures = 2;
    public const int MaxLessonsPerSweep = 100;
    public const int MaxRunsPerTeamSweep = 500;

    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<LessonQualifier> _logger;

    public LessonQualifier(CodeSpaceDbContext db, ILogger<LessonQualifier> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> QualifyAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('codespace.lesson_qualification'))", cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var lessons = await _db.Lesson
            .Where(lesson => lesson.InvalidatedAt == null && lesson.ValidFrom <= now && lesson.ExpiresAt > now)
            .OrderBy(lesson => lesson.QualificationCheckedAt).ThenBy(lesson => lesson.Id)
            .Take(MaxLessonsPerSweep)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var changed = 0;

        foreach (var group in lessons.GroupBy(lesson => lesson.TeamId))
            changed += await QualifyTeamAsync(group.Key, group.ToList(), now, cancellationToken).ConfigureAwait(false);

        foreach (var lesson in lessons) lesson.QualificationCheckedAt = now;
        if (lessons.Count > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private async Task<int> QualifyTeamAsync(Guid teamId, IReadOnlyList<Lesson> lessons, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var earliest = lessons.Min(lesson => lesson.ValidFrom);
        var rows = await (from score in _db.RunScorecard.AsNoTracking()
                          join run in _db.WorkflowRun.AsNoTracking() on new { score.WorkflowRunId, score.TeamId } equals new { WorkflowRunId = run.Id, run.TeamId }
                          where score.TeamId == teamId && run.Purpose == null && score.LessonArm == LessonArms.Injected
                          where score.ScorerVersion == UnattendedDeliveryScorer.ScorerVersion && score.CompletedAt >= earliest && score.CompletedAt <= now
                          orderby score.CompletedAt descending, score.WorkflowRunId
                          select new ScoredRun(score.WorkflowRunId, score.CompletedAt, score.UnattendedSolvedWithDelivery))
            .Take(MaxRunsPerTeamSweep)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var exposures = await RunLessonExposures.ReadAsync(_db, rows.Select(row => row.RunId).ToList(), teamId, cancellationToken).ConfigureAwait(false);
        var changed = 0;

        foreach (var lesson in lessons)
        {
            var observed = rows.Where(row => row.CompletedAt >= lesson.ValidFrom && row.CompletedAt < lesson.ExpiresAt && exposures.TryGetValue(row.RunId, out var ids) && ids.Contains(lesson.Id)).ToList();
            var successes = lesson.SuccessfulExposureRunIds.ToHashSet();
            var negatives = lesson.NegativeExposureRunIds.ToHashSet();

            foreach (var row in observed)
            {
                if (row.UnattendedSolvedWithDelivery)
                {
                    successes.Add(row.RunId);
                    negatives.Remove(row.RunId);
                }
                else
                {
                    negatives.Add(row.RunId);
                    successes.Remove(row.RunId);
                }
            }

            var suppressed = negatives.Count >= MinimumSuccessfulExposures && negatives.Count >= successes.Count;
            var qualified = !suppressed && successes.Count >= MinimumSuccessfulExposures && successes.Count > negatives.Count;
            if (successes.SetEquals(lesson.SuccessfulExposureRunIds) && negatives.SetEquals(lesson.NegativeExposureRunIds) && qualified == (lesson.QualifiedAt != null) && suppressed == (lesson.QualificationSuppressedAt != null)) continue;

            lesson.SuccessfulExposureRunIds = successes.OrderBy(id => id).ToList();
            lesson.NegativeExposureRunIds = negatives.OrderBy(id => id).ToList();
            lesson.QualifiedAt = qualified ? lesson.QualifiedAt ?? now : null;
            lesson.QualificationSuppressedAt = suppressed ? lesson.QualificationSuppressedAt ?? now : null;
            changed++;
        }

        if (rows.Count == MaxRunsPerTeamSweep)
            _logger.LogWarning("Lesson qualification for team {TeamId} reached its {Cap}-run bound; older unrecorded outcomes remain unclaimed rather than being inferred", teamId, MaxRunsPerTeamSweep);

        return changed;
    }

    private sealed record ScoredRun(Guid RunId, DateTimeOffset CompletedAt, bool UnattendedSolvedWithDelivery);
}
