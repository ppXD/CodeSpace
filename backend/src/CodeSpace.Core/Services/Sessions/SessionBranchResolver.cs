using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionBranchResolver"/>. Scans the session's recent top-level turns that targeted the repo,
/// newest first, and returns the newest run's produced branch from its <c>OutputsJson</c> (the single-agent terminal
/// surfaces <c>branch</c>). The partial <c>idx_workflow_run_session</c> index (migration 0070) keeps the lookup cheap;
/// the scan is bounded since the latest produced branch is always in a recent turn.
/// <para>v1 covers the single-repo case (the run's <c>branch</c> output). Per-repo continuity for a multi-repo run
/// (reading <c>AgentRunResult.RepositoryResults[].ProducedBranch</c>) and resolving a deep/standard run's integrated
/// branch are follow-ons — a turn that surfaces no <c>branch</c> output simply contributes none, so the scan skips
/// to the last turn that did (the safe, correct fallback).</para>
/// </summary>
public sealed class SessionBranchResolver : ISessionBranchResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    /// <summary>Bound the newest-first scan — the latest produced branch is in a recent turn; an all-analysis tail beyond this is vanishingly rare and degrades to the safe base-branch fallback.</summary>
    internal const int MaxTurnsScanned = 50;

    public SessionBranchResolver(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<string?> ResolveStartRefAsync(Guid sessionId, Guid teamId, Guid primaryRepositoryId, CancellationToken cancellationToken)
    {
        var recent = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == teamId && r.SessionTurnIndex != null && r.ScopeRepositoryIds.Contains(primaryRepositoryId))
            .OrderByDescending(r => r.SessionTurnIndex)
            .Take(MaxTurnsScanned)
            .Select(r => new { r.ScopeRepositoryIds, r.OutputsJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in recent)
        {
            // v1 attributes a branch ONLY from a SINGLE-repo turn: its OutputsJson.branch is unambiguously THIS repo's.
            // A multi-repo turn surfaces only the PRIMARY's branch, so attributing it to a non-primary repo would clone
            // the wrong branch — those carry per-repo branches in AgentRunResult.RepositoryResults (the noted follow-on).
            if (row.ScopeRepositoryIds.Count != 1) continue;

            var branch = ReadBranch(row.OutputsJson);
            if (branch != null) return branch;
        }

        return null;
    }

    /// <summary>Read the produced <c>branch</c> from a run's declared outputs, tolerating malformed / non-object / absent JSON (returns null).</summary>
    private static string? ReadBranch(string outputsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("branch", out var v)
                   && v.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
