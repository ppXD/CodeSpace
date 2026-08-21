using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>Pure grouping/effective-attempt projection for the narrow Room/Journal read model.</summary>
internal static class SessionSkeletonProjection
{
    internal sealed record RunRow(
        Guid Id, Guid? RootRunId, int? SessionTurnIndex, WorkflowRunStatus Status, string? ProjectionKind,
        string SourceType, string? RerunFromNodeId, DateTimeOffset CreatedDate, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, string? Error, string? Goal, string? Result, bool HasPendingDecision);

    internal static IReadOnlyList<SessionTurn> BuildTurns(IEnumerable<RunRow> runs)
    {
        var turns = new List<SessionTurn>();

        foreach (var group in runs.GroupBy(row => row.RootRunId ?? row.Id))
        {
            var ordered = group.OrderBy(row => row.CreatedDate).ThenBy(row => row.Id).ToList();
            var turnRun = ordered.FirstOrDefault(row => row.SessionTurnIndex != null);
            if (turnRun == null) continue;

            var effectiveId = SessionTurnAttempts.ResolveEffectiveId(ordered.Select(row => new SessionTurnAttempts.AttemptRow(row.Id, row.Status, row.CreatedDate)));
            var effective = ordered.Single(row => row.Id == effectiveId);

            turns.Add(new SessionTurn
            {
                TurnIndex = turnRun.SessionTurnIndex!.Value,
                TurnRunId = turnRun.Id,
                RunId = effective.Id,
                UserMessage = turnRun.Goal,
                RunStatus = effective.Status,
                ProjectionKind = effective.ProjectionKind,
                Result = effective.Result == null ? null : SessionTurnText.Clip(effective.Result),
                ProducedBranch = null,
                RepositoryResults = null,
                HasPendingDecision = effective.HasPendingDecision,
                CreatedDate = turnRun.CreatedDate,
                StartedAt = effective.StartedAt,
                CompletedAt = effective.CompletedAt,
                Error = effective.Error,
                AttemptCount = ordered.Count,
                Attempts = ordered.Count > 1 ? BuildLadder(ordered, effective.Id) : null,
            });
        }

        return turns.OrderBy(turn => turn.TurnIndex).ToList();
    }

    private static IReadOnlyList<SessionTurnAttempt> BuildLadder(IReadOnlyList<RunRow> ordered, Guid effectiveId) =>
        ordered.Select((row, index) => new SessionTurnAttempt
        {
            RunId = row.Id,
            AttemptNumber = index + 1,
            Status = row.Status,
            SourceType = row.SourceType,
            RerunFromNodeId = row.RerunFromNodeId,
            CreatedDate = row.CreatedDate,
            IsLatest = row.Id == effectiveId,
            Error = row.Error,
        }).ToList();
}
