using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Resolves a run's produced branch(es) from its <see cref="PublishManifest"/> rows (I2: the single source of truth)
/// — the shared choke point <see cref="SessionProjection"/> (the turn's own display) and
/// <see cref="SessionBranchResolver"/> (the NEXT turn's clone ref) both go through, so the two can never disagree on
/// what a run produced. Pure (no DB) — the caller loads the manifest rows.
///
/// <para>A supervisor turn's per-subtask <see cref="PublishManifestKind.Agent"/> rows are internal fan-out detail, not
/// the turn's own outcome — an <see cref="PublishManifestKind.Integration"/> row (the fold), when one exists, is
/// authoritative over them. A plain single-agent turn has no Integration row at all (there is nothing to fold), so its
/// one Agent row IS the turn's outcome. Rows with no live pushed branch (patch-only, none, or Failed) never count —
/// they carry no ref a next turn could clone from.</para>
/// </summary>
internal static class SessionManifestBranches
{
    /// <summary>The manifest rows that actually describe a live, cloneable branch for this run — Integration rows win over Agent rows when both exist (the fold supersedes its own inputs), and a retried/duplicate subtask's repeated rows for the SAME repository collapse to just its newest.</summary>
    private static IReadOnlyList<PublishManifest> Authoritative(IReadOnlyList<PublishManifest>? manifests)
    {
        if (manifests is not { Count: > 0 }) return Array.Empty<PublishManifest>();

        var pushed = manifests.Where(m => m.PublishStateValue == PublishState.Pushed && !string.IsNullOrEmpty(m.Branch)).ToList();
        if (pushed.Count == 0) return Array.Empty<PublishManifest>();

        var integrated = pushed.Where(m => m.Kind == PublishManifestKind.Integration).ToList();
        return DeduplicateByRepository(integrated.Count > 0 ? integrated : pushed);
    }

    /// <summary>Collapse repeated rows for the SAME repository (e.g. a retried subtask re-published) to just the newest — a repo can only ever have produced ONE live branch at a time. Rows with no resolvable repository id are never deduplicated against each other (each is kept, since none can be attributed to a specific repo anyway).</summary>
    private static IReadOnlyList<PublishManifest> DeduplicateByRepository(IReadOnlyList<PublishManifest> rows)
    {
        if (rows.Count <= 1) return rows;

        return rows.GroupBy(m => m.RepositoryId).SelectMany(g => g.Key is null ? g : g.OrderByDescending(m => m.CreatedDate).Take(1)).ToList();
    }

    /// <summary>The FLAT single-repo branch — set only when the run produced exactly ONE live branch (a multi-repo run's rows surface through <see cref="ResolveRepositoryBranches"/> instead, never here — mirrors the pre-existing OutputsJson.branch / repositoryResults[] mutual exclusivity).</summary>
    internal static string? ResolveSingleRepoBranch(IReadOnlyList<PublishManifest>? manifests)
    {
        var rows = Authoritative(manifests);
        return rows.Count == 1 ? rows[0].Branch : null;
    }

    /// <summary>The per-repo (repositoryId, branch) pairs for a MULTI-repo run — empty when the run produced 0 or exactly 1 live branch (the single case surfaces through <see cref="ResolveSingleRepoBranch"/> instead). A row with no resolvable repository id is skipped — it can't be attributed to a specific repo.</summary>
    internal static IReadOnlyList<(Guid RepositoryId, string Branch)> ResolveRepositoryBranches(IReadOnlyList<PublishManifest>? manifests)
    {
        var rows = Authoritative(manifests);
        if (rows.Count <= 1) return Array.Empty<(Guid, string)>();

        return rows.Where(m => m.RepositoryId is not null).Select(m => (m.RepositoryId!.Value, m.Branch!)).ToList();
    }
}
