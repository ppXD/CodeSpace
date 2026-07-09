using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Pure projection from a session's loaded run rows to the conversation DTOs — the grouping / effective-attempt /
/// attempt-ladder logic, kept side-effect-free so it is unit-tested directly (the read service only loads the bounded
/// row sets + the pending-decision set, then delegates here). A TURN = the lineage of one top-level run
/// (<c>RootRunId ?? Id</c>), its rerun forks nested as attempts; the turn's headline outcome is its EFFECTIVE attempt
/// per <see cref="SessionTurnAttempts"/> (the newest SUCCEEDED attempt, else the newest overall — never a superseded
/// failure masking a later rerun's success). Reuses <see cref="SessionTurnText"/> for the goal / result reads — the one
/// place a turn's clean text is parsed (Rule 7).
/// </summary>
internal static class SessionProjection
{
    /// <summary>A run row loaded for the DETAIL projection — only the columns the turns need.</summary>
    internal sealed record RunRow(
        Guid Id, Guid? RootRunId, int? SessionTurnIndex, WorkflowRunStatus Status, string? ProjectionKind,
        string SourceType, string? RerunFromNodeId, DateTimeOffset CreatedDate, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, string? Error, string OutputsJson, string GoalPayload, IReadOnlyList<Guid> ScopeRepositoryIds);

    /// <summary>
    /// Group a session's runs into ordered turns. A turn is a lineage (<c>RootRunId ?? Id</c>) that CONTAINS a top-level
    /// run (a non-null <c>SessionTurnIndex</c>); pure attempt forks carry a null turn index and nest under it. A lineage
    /// with no top-level member (an orphaned child / replay whose root isn't itself a turn) is skipped. The turn's
    /// headline outcome is its EFFECTIVE attempt per <see cref="SessionTurnAttempts"/>; the ladder is oldest → newest
    /// regardless. Ordered by turn ordinal.
    /// </summary>
    /// <param name="manifestsByRunId">
    /// The latest attempt's own <see cref="PublishManifest"/> rows, keyed by run id (I2) — the caller bulk-loads these
    /// (<c>IPublishManifestStore.ListForWorkflowRunsAsync</c>) so this projection stays pure/DB-free. Preferred over
    /// the legacy raw <c>OutputsJson.branch</c> / <c>repositoryResults[]</c> read; a run with no manifest rows (older
    /// data, or a supervisor fold nobody has opened a PR for yet — <c>RoomPullRequestService</c> is still the only
    /// writer of an Integration-kind row today) falls back to the legacy read, never a silent blank.
    /// </param>
    internal static IReadOnlyList<SessionTurn> BuildTurns(IEnumerable<RunRow> runs, ISet<Guid> pendingDecisionRunIds, IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>? manifestsByRunId = null)
    {
        var turns = new List<SessionTurn>();

        foreach (var group in runs.GroupBy(r => r.RootRunId ?? r.Id))
        {
            var ordered = group.OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).ToList();

            var turnRun = ordered.FirstOrDefault(r => r.SessionTurnIndex != null);
            if (turnRun == null) continue;   // not a top-level turn — skip (orphaned attempts)

            var effectiveId = SessionTurnAttempts.ResolveEffectiveId(ordered.Select(r => new SessionTurnAttempts.AttemptRow(r.Id, r.Status, r.CreatedDate)));
            var effective = ordered.Single(r => r.Id == effectiveId);
            var manifests = manifestsByRunId?.GetValueOrDefault(effective.Id);

            var manifestRepoResults = SessionManifestBranches.ResolveRepositoryBranches(manifests);
            var repositoryResults = manifestRepoResults.Count > 0
                ? manifestRepoResults.Select(b => new SessionTurnRepoResult { RepositoryId = b.RepositoryId, ProducedBranch = b.Branch }).ToList()
                : ReadRepositoryResults(effective.OutputsJson);

            var producedBranch = repositoryResults == null
                ? SessionManifestBranches.ResolveSingleRepoBranch(manifests) ?? SessionTurnText.ReadString(effective.OutputsJson, "branch")
                : null;

            turns.Add(new SessionTurn
            {
                TurnIndex = turnRun.SessionTurnIndex!.Value,
                TurnRunId = turnRun.Id,
                RunId = effective.Id,
                UserMessage = SessionTurnText.ReadString(turnRun.GoalPayload, "goal"),
                RunStatus = effective.Status,
                ProjectionKind = effective.ProjectionKind,
                Result = ClipResult(effective.OutputsJson),
                ProducedBranch = producedBranch,
                RepositoryResults = repositoryResults,
                HasPendingDecision = pendingDecisionRunIds.Contains(effective.Id),
                CreatedDate = turnRun.CreatedDate,
                StartedAt = effective.StartedAt,
                CompletedAt = effective.CompletedAt,
                Error = effective.Error,
                AttemptCount = ordered.Count,
                Attempts = ordered.Count > 1 ? BuildLadder(ordered, effective.Id) : null,
            });
        }

        return turns.OrderBy(t => t.TurnIndex).ToList();
    }

    /// <summary>The oldest→newest attempt ladder — <see cref="SessionTurnAttempt.IsLatest"/> marks the EFFECTIVE attempt (the one the turn shows, per <paramref name="effectiveId"/>), never merely the chronologically-newest one.</summary>
    private static IReadOnlyList<SessionTurnAttempt> BuildLadder(IReadOnlyList<RunRow> ordered, Guid effectiveId) =>
        ordered.Select((r, i) => new SessionTurnAttempt
        {
            RunId = r.Id,
            AttemptNumber = i + 1,
            Status = r.Status,
            SourceType = r.SourceType,
            RerunFromNodeId = r.RerunFromNodeId,
            CreatedDate = r.CreatedDate,
            IsLatest = r.Id == effectiveId,
            Error = r.Error,
        }).ToList();

    private static string? ClipResult(string outputsJson)
    {
        var result = SessionTurnText.ReadResult(outputsJson);
        return result == null ? null : SessionTurnText.Clip(result);
    }

    /// <summary>
    /// The per-repo produced branches of a MULTI-repo turn (<c>OutputsJson.repositoryResults[]</c>, each entry
    /// { repositoryId, producedBranch }). Null when the key is absent (a single-repo turn surfaces its branch in the flat
    /// <c>branch</c> key instead), the array is empty, or the JSON is malformed — so single-repo and multi-repo turns
    /// are unambiguous (a single-repo turn never has the array). Entries missing an id / branch are skipped.
    /// </summary>
    internal static IReadOnlyList<SessionTurnRepoResult>? ReadRepositoryResults(string outputsJson)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }

        if (!root.TryGetProperty("repositoryResults", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;

        var results = new List<SessionTurnRepoResult>();
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("repositoryId", out var idEl) || idEl.ValueKind != JsonValueKind.String || !Guid.TryParse(idEl.GetString(), out var repoId)) continue;

            var branch = entry.TryGetProperty("producedBranch", out var b) && b.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b.GetString())
                ? b.GetString()
                : null;
            if (branch != null) results.Add(new SessionTurnRepoResult { RepositoryId = repoId, ProducedBranch = branch });
        }

        return results.Count > 0 ? results : null;
    }

    /// <summary>A run row loaded for the LIST's latest-run-per-session signal.</summary>
    internal sealed record SessionRunRow(Guid SessionId, Guid Id, WorkflowRunStatus Status, string? ProjectionKind, DateTimeOffset CreatedDate, Guid? RootRunId, int? SessionTurnIndex);

    /// <summary>The session's latest TURN's EFFECTIVE attempt (I2-consistent with <see cref="BuildTurns"/>) — the list's live-status badge + deep-link target, so it never disagrees with the detail view it links to.</summary>
    internal static Dictionary<Guid, SessionRunRow> LatestRunBySession(IEnumerable<SessionRunRow> runs) =>
        runs.GroupBy(r => r.SessionId).ToDictionary(g => g.Key, LatestTurnEffectiveRun);

    private static SessionRunRow LatestTurnEffectiveRun(IEnumerable<SessionRunRow> sessionRuns)
    {
        var rows = sessionRuns.ToList();

        var latestTurnRun = rows.Where(r => r.SessionTurnIndex != null).OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).LastOrDefault();
        var lineage = latestTurnRun == null ? rows : rows.Where(r => (r.RootRunId ?? r.Id) == latestTurnRun.Id).ToList();

        var effectiveId = SessionTurnAttempts.ResolveEffectiveId(lineage.Select(r => new SessionTurnAttempts.AttemptRow(r.Id, r.Status, r.CreatedDate)));
        return lineage.Single(r => r.Id == effectiveId);
    }
}
