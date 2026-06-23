using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionBranchResolver"/>. Scans the session's recent top-level turns newest first and, for each
/// requested repo, returns the newest turn's produced branch for it. A single-repo turn surfaces its one repo's branch
/// in <c>OutputsJson.branch</c>; a multi-repo turn surfaces every writable repo's branch in
/// <c>OutputsJson.repositoryResults[]</c> (each entry's <c>repositoryId</c> + <c>producedBranch</c>). The partial
/// <c>idx_workflow_run_session</c> index (migration 0070) keeps the lookup cheap; the scan is bounded since the latest
/// produced branch is always in a recent turn.
/// <para>A turn that surfaces no branch for a repo (an analysis-only turn, or a plan-map turn whose terminal carries
/// only the synthesized text) simply contributes none, so the scan skips to the last turn that did — the safe, correct
/// fallback (an absent repo ⇒ its default branch). Reading <c>repositoryResults</c> generically also fixes the v1
/// single-repo-only limitation: a multi-repo prior turn now contributes the primary's branch too.</para>
/// </summary>
public sealed class SessionBranchResolver : ISessionBranchResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    /// <summary>Bound the newest-first scan — the latest produced branch is in a recent turn; an all-analysis tail beyond this is vanishingly rare and degrades to the safe default-branch fallback.</summary>
    internal const int MaxTurnsScanned = 50;

    private static readonly IReadOnlyDictionary<Guid, string> Empty = new Dictionary<Guid, string>();

    public SessionBranchResolver(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveStartRefsAsync(Guid sessionId, Guid teamId, IReadOnlyCollection<Guid> repositoryIds, CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0) return Empty;

        var recent = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == teamId && r.SessionTurnIndex != null)
            .OrderByDescending(r => r.SessionTurnIndex)
            .Take(MaxTurnsScanned)
            .Select(r => new { r.ScopeRepositoryIds, r.OutputsJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var wanted = new HashSet<Guid>(repositoryIds);
        var resolved = new Dictionary<Guid, string>();

        foreach (var row in recent)   // newest first — the first turn that produced a repo's branch wins
        {
            if (resolved.Count == wanted.Count) break;

            foreach (var (repoId, branch) in ReadProducedBranches(row.OutputsJson, row.ScopeRepositoryIds))
            {
                if (wanted.Contains(repoId) && !resolved.ContainsKey(repoId)) resolved[repoId] = branch;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The (repositoryId → produced branch) pairs a turn's declared outputs surface. A MULTI-repo turn lists every
    /// writable repo in <c>repositoryResults[]</c> ({ repositoryId, producedBranch }); a SINGLE-repo turn surfaces its
    /// one repo's branch in the flat <c>branch</c> key, attributed to its sole scope repo. Tolerates malformed /
    /// non-object / absent JSON (yields nothing). The multi-repo array, when present, is authoritative — a single-repo
    /// turn never has it (so the flat branch is unambiguous).
    /// </summary>
    internal static IEnumerable<(Guid repoId, string branch)> ReadProducedBranches(string outputsJson, IReadOnlyList<Guid> scopeRepositoryIds)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root.TryGetProperty("repositoryResults", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
        {
            foreach (var entry in results.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("repositoryId", out var idEl) || idEl.ValueKind != JsonValueKind.String || !Guid.TryParse(idEl.GetString(), out var repoId)) continue;

                var branch = ReadNonBlankString(entry, "producedBranch");
                if (branch != null) yield return (repoId, branch);
            }

            yield break;   // a multi-repo turn's per-repo array is authoritative — never also attribute the flat branch
        }

        // Single-repo turn: the flat branch is unambiguously this turn's one scope repo's (S4b path, unchanged).
        if (scopeRepositoryIds.Count == 1)
        {
            var branch = ReadNonBlankString(root, "branch");
            if (branch != null) yield return (scopeRepositoryIds[0], branch);
        }
    }

    /// <summary>Read a non-blank string property, else null.</summary>
    private static string? ReadNonBlankString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}
