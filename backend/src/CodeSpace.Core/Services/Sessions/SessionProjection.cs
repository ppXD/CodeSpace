using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Pure projection from a session's loaded run rows to the conversation DTOs — the grouping / latest-wins / attempt-
/// ladder logic, kept side-effect-free so it is unit-tested directly (the read service only loads the bounded row sets
/// + the pending-decision set, then delegates here). A TURN = the lineage of one top-level run (<c>RootRunId ?? Id</c>),
/// its rerun forks nested as attempts; the turn shows its NEWEST attempt's outcome (latest-wins, matching the runs
/// index's collapse). Reuses <see cref="SessionTurnText"/> for the goal / result reads — the one place a turn's clean
/// text is parsed (Rule 7).
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
    /// with no top-level member (an orphaned child / replay whose root isn't itself a turn) is skipped. Latest-wins: the
    /// turn's headline outcome is its newest attempt's; the ladder is oldest → newest. Ordered by turn ordinal.
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

            var latest = ordered[^1];
            var manifests = manifestsByRunId?.GetValueOrDefault(latest.Id);

            var manifestRepoResults = SessionManifestBranches.ResolveRepositoryBranches(manifests);
            var repositoryResults = manifestRepoResults.Count > 0
                ? manifestRepoResults.Select(b => new SessionTurnRepoResult { RepositoryId = b.RepositoryId, ProducedBranch = b.Branch }).ToList()
                : ReadRepositoryResults(latest.OutputsJson);

            var producedBranch = repositoryResults == null
                ? SessionManifestBranches.ResolveSingleRepoBranch(manifests) ?? SessionTurnText.ReadString(latest.OutputsJson, "branch")
                : null;

            turns.Add(new SessionTurn
            {
                TurnIndex = turnRun.SessionTurnIndex!.Value,
                TurnRunId = turnRun.Id,
                RunId = latest.Id,
                UserMessage = SessionTurnText.ReadString(turnRun.GoalPayload, "goal"),
                RunStatus = latest.Status,
                ProjectionKind = latest.ProjectionKind,
                Result = ClipResult(latest.OutputsJson),
                ProducedBranch = producedBranch,
                RepositoryResults = repositoryResults,
                HasPendingDecision = pendingDecisionRunIds.Contains(latest.Id),
                CreatedDate = turnRun.CreatedDate,
                StartedAt = latest.StartedAt,
                CompletedAt = latest.CompletedAt,
                Error = latest.Error,
                AttemptCount = ordered.Count,
                Attempts = ordered.Count > 1 ? BuildLadder(ordered) : null,
            });
        }

        return turns.OrderBy(t => t.TurnIndex).ToList();
    }

    private static IReadOnlyList<SessionTurnAttempt> BuildLadder(IReadOnlyList<RunRow> ordered) =>
        ordered.Select((r, i) => new SessionTurnAttempt
        {
            RunId = r.Id,
            AttemptNumber = i + 1,
            Status = r.Status,
            SourceType = r.SourceType,
            RerunFromNodeId = r.RerunFromNodeId,
            CreatedDate = r.CreatedDate,
            IsLatest = i == ordered.Count - 1,
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
    internal sealed record SessionRunRow(Guid SessionId, Guid Id, WorkflowRunStatus Status, string? ProjectionKind, DateTimeOffset CreatedDate);

    /// <summary>The newest run per session (max <c>(CreatedDate, Id)</c>) — the list's live-status badge + deep-link target.</summary>
    internal static Dictionary<Guid, SessionRunRow> LatestRunBySession(IEnumerable<SessionRunRow> runs) =>
        runs.GroupBy(r => r.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).Last());
}
