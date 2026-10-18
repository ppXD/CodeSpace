using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Failures;

/// <summary>
/// Reads the provider failure a run ended on from the two places it is written: the run's own ledger (a node that threw it, a
/// run that could not start) and the agent run's stored result (a clone that could not authenticate, a push the remote refused).
/// Bounded, and never throws on a record it cannot read — an old row degrades to "no failure", which keeps the page rendering the
/// sentence it always did.
///
/// <para>It answers for the node that ENDED the run, and for nothing earlier. A run walks past failures it handles (an error
/// edge), so an older record that names a provider failure says only that the run once met one; the run's own error names the node
/// that failed it (<c>Node 'x' failed.</c>), and that node's record decides — a failure carried there, else the agent run it spawned.
/// A node that failed for another reason, or a run ended by the reconciler, answers null rather than the first failure found.</para>
/// </summary>
public sealed class RunFailureReader : IRunFailureReader, IScopedDependency
{
    /// <summary>How many of a run's failure records are looked through for one that names a failure. A run records a handful; the bound is for a map that failed a thousand branches.</summary>
    private const int MaxRecordScan = 40;

    private readonly CodeSpaceDbContext _db;

    public RunFailureReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<RecordedFailure?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (run is null) return null;

        var found = run.Status == WorkflowRunStatus.Failure
            ? await FromWhereTheRunEndedAsync(runId, teamId, run.Error, cancellationToken).ConfigureAwait(false)
            : await FromLatestAgentRunAsync(runId, teamId, nodeId: null, publishRefusalOnly: true, cancellationToken).ConfigureAwait(false);

        return found is null ? null : new RecordedFailure { Failure = found.Value.Failure, At = found.Value.At, ScopeRepositoryIds = run.ScopeRepositoryIds };
    }

    private async Task<RunHeader?> LoadRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId && r.TeamId == teamId)
            .Select(r => new RunHeader(r.Status, r.ScopeRepositoryIds, r.Error))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The failure the node that ended the run recorded; failing that, its agent run's own account. Where the ledger names no node that ended the run, nothing speaks for it — except a ledger with no node failure at all, which leaves the agent run as the only witness.</summary>
    private async Task<(PersistedFailure Failure, DateTimeOffset At)?> FromWhereTheRunEndedAsync(Guid runId, Guid teamId, string? runError, CancellationToken cancellationToken)
    {
        var ended = await ReadLedgerEndAsync(runId, runError, cancellationToken).ConfigureAwait(false);

        if (ended.Failure is { } failure) return (failure, ended.At);

        return ended.NodeFailuresRecorded && ended.NodeId is null ? null : await FromLatestAgentRunAsync(runId, teamId, ended.NodeId, publishRefusalOnly: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Newest first: a <c>run.failed</c> that carries a failure of its own (a run that could not start) is the answer; one that
    /// does not only restates, in a sentence, the node that failed it, so it is passed over to the node the run's error names. A
    /// node that failed and was handled, or failed inside a branch that did not end the run, is not that node and is passed over.
    /// </summary>
    private async Task<LedgerEnd> ReadLedgerEndAsync(Guid runId, string? runError, CancellationToken cancellationToken)
    {
        var rows = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && (r.RecordType == WorkflowRunRecordTypes.NodeFailed || r.RecordType == WorkflowRunRecordTypes.RunFailed))
            .OrderByDescending(r => r.Sequence)
            .Select(r => new { r.RecordType, r.NodeId, r.PayloadJson, r.OccurredAt })
            .Take(MaxRecordScan)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var failure = PersistedFailureJson.Read(row.PayloadJson);

            if (row.RecordType == WorkflowRunRecordTypes.RunFailed)
            {
                if (failure is not null) return new LedgerEnd(failure, row.OccurredAt, null, true);

                continue;
            }

            if (!NamesNode(runError, row.NodeId)) continue;

            return new LedgerEnd(failure, row.OccurredAt, row.NodeId, true);
        }

        return new LedgerEnd(null, default, null, rows.Any(r => r.RecordType == WorkflowRunRecordTypes.NodeFailed));
    }

    /// <summary>The walker ends a run with <c>Node 'x' failed.</c>; a run whose error says nothing (none recorded) leaves every node a candidate.</summary>
    private static bool NamesNode(string? runError, string? nodeId) => string.IsNullOrEmpty(runError) || (nodeId is not null && runError.Contains($"'{nodeId}'", StringComparison.Ordinal));

    /// <summary>
    /// The latest agent run's own account — of <paramref name="nodeId"/>'s runs when the ledger named the node that ended the run, since
    /// another node's agent run is not why this one ended. Only the LAST one: an earlier attempt that met a disconnected credential
    /// and was respawned says nothing about why the run ended. <paramref name="publishRefusalOnly"/> narrows it to the one failure a
    /// run that finished can carry — its branch was refused by the remote.
    /// </summary>
    private async Task<(PersistedFailure Failure, DateTimeOffset At)?> FromLatestAgentRunAsync(Guid runId, Guid teamId, string? nodeId, bool publishRefusalOnly, CancellationToken cancellationToken)
    {
        var latest = await _db.AgentRun.AsNoTracking()
            .Where(a => a.WorkflowRunId == runId && a.TeamId == teamId && a.ResultJson != null && (nodeId == null || a.NodeId == nodeId))
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => new { a.ResultJson, At = a.CompletedAt ?? a.LastModifiedDate })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (latest is null) return null;

        var failure = ReadAgentResult(latest.ResultJson!);

        if (failure is null || (publishRefusalOnly && failure.Code != FailureCodes.GitCredentialRejected)) return null;

        return (failure, latest.At);
    }

    /// <summary>The failure an agent run's stored result names: its own <c>failure</c>, else a push refused for the credential — the single-repo result's, or a multi-repo result's first refused repository.</summary>
    internal static PersistedFailure? ReadAgentResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var result = document.RootElement;

            if (result.ValueKind != JsonValueKind.Object) return null;
            if (result.TryGetProperty(PersistedFailureJson.PayloadKey, out var own) && PersistedFailureJson.ReadElement(own) is { } failure) return failure;
            if (ReadString(result, "publishFailureCode") == FailureCodes.GitCredentialRejected) return Refused(repositoryId: null);

            return RefusedRepository(result);
        }
        catch (JsonException) { return null; }
    }

    private static PersistedFailure? RefusedRepository(JsonElement result)
    {
        if (!result.TryGetProperty("repositoryResults", out var repositories) || repositories.ValueKind != JsonValueKind.Array) return null;

        foreach (var repository in repositories.EnumerateArray())
            if (repository.ValueKind == JsonValueKind.Object && ReadString(repository, "publishFailureCode") == FailureCodes.GitCredentialRejected)
                return Refused(Guid.TryParse(ReadString(repository, "repositoryId"), out var id) ? id : null);

        return null;
    }

    private static PersistedFailure Refused(Guid? repositoryId) => new() { Code = FailureCodes.GitCredentialRejected, RepositoryId = repositoryId };

    private static string? ReadString(JsonElement bag, string key) => bag.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record RunHeader(WorkflowRunStatus Status, List<Guid> ScopeRepositoryIds, string? Error);

    /// <summary>What the ledger says ended the run: the failure the node (or the run record) carries, when it carries one; which node that was, when one is named; and whether any node failed at all.</summary>
    private sealed record LedgerEnd(PersistedFailure? Failure, DateTimeOffset At, string? NodeId, bool NodeFailuresRecorded);
}
