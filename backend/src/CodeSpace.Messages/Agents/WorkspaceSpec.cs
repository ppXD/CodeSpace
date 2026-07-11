using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The AUTHORED intent of an agent's workspace (multi-repo PR1, Rule 18.1 — a pure data noun): WHICH repositories
/// the agent works across, each repo's access (read context vs writable), and where the harness's cwd points. The
/// generic底座 the single-repo case is just a special case of: <c>Repositories.Count == 1</c>.
///
/// <para>This is the authored shape; the resolver turns each <see cref="WorkspaceRepositorySpec"/> into a concrete
/// per-repo clone instruction (<c>WorkspaceRequest</c>). Distinct from <c>AgentTask.RepositoryId</c> (the legacy
/// single-repo field): a task with a null <c>Workspace</c> derives one via <see cref="FromRepository"/>, so an
/// existing single-repo run is byte-identical.</para>
///
/// <para>THE SINGLE-REPO RUNTIME INVARIANT: a one-repo workspace runs the harness with cwd = that repo's root
/// (NOT a <c>/workspace/&lt;alias&gt;</c> subdir), matching how Claude/Codex behave when opened on a repo folder.
/// Only a genuine multi-repo workspace (&gt;1 repo) clones under a shared root and points cwd at the root.</para>
/// </summary>
public sealed record WorkspaceSpec
{
    /// <summary>The repositories the agent's workspace contains, in authored order. Always at least one. A single-element list is the common (legacy single-repo) case.</summary>
    public required IReadOnlyList<WorkspaceRepositorySpec> Repositories { get; init; }

    /// <summary>The alias of the PRIMARY repo (the one cwd resolves to in single-repo / <see cref="WorkspaceCwdMode.PrimaryRepo"/>, and the source of the legacy compat result fields). Null → derived: the <see cref="WorkspaceRepositorySpec.IsPrimary"/> repo, else the first writable, else the first.</summary>
    public string? PrimaryAlias { get; init; }

    /// <summary>Where the harness runs. <see cref="WorkspaceCwdMode.Auto"/> (default) = repo-root for one repo, workspace-root for many.</summary>
    public WorkspaceCwdMode CwdMode { get; init; } = WorkspaceCwdMode.Auto;

    /// <summary>The default alias a single-repo workspace uses (the legacy <c>RepositoryId</c> path).</summary>
    public const string DefaultAlias = "repo";

    /// <summary>
    /// Build the single-repo workspace an existing <c>AgentTask.RepositoryId</c> implies — one writable primary repo
    /// at the default alias. The back-compat bridge so every single-repo run resolves through the SAME canonical
    /// <see cref="WorkspaceSpec"/> as a multi-repo one, with byte-identical execution. <paramref name="pinnedSha"/>
    /// (S1) pins the primary to an exact base commit (see <see cref="WorkspaceRepositorySpec.PinnedSha"/>).
    /// </summary>
    public static WorkspaceSpec FromRepository(Guid repositoryId, string? @ref = null, bool refSoftFallback = false, string? pinnedSha = null) => new()
    {
        Repositories = new[]
        {
            new WorkspaceRepositorySpec { Alias = DefaultAlias, RepositoryId = repositoryId, Ref = @ref, RefSoftFallback = refSoftFallback, PinnedSha = pinnedSha, Path = DefaultAlias, Access = WorkspaceAccess.Write, IsPrimary = true },
        },
        PrimaryAlias = DefaultAlias,
        CwdMode = WorkspaceCwdMode.Auto,
    };

    /// <summary>
    /// Build the AUTHORED multi-repo workspace from a primary repo + a list of related repos — the centralization
    /// point every producer (the agent.run node, the projection builders) funnels through so the projection logic
    /// lives in ONE place. Returns NULL when there are NO related repos, so a caller does
    /// <c>Workspace = FromAuthoredRepos(primaryId, ref, related)</c> and a no-related-repos run keeps <c>Workspace</c>
    /// null → the resolver falls back to <see cref="FromRepository"/> → BYTE-IDENTICAL single-repo execution.
    ///
    /// <para>The primary keeps the exact <see cref="FromRepository"/> defaults (alias "repo", writable, primary) so a
    /// one-related-repo workspace's primary repo runs identically. Each related repo gets a unique alias (its authored
    /// alias, else <c>repo-2</c>, <c>repo-3</c>, …) defaulting to read-only context unless authored writable.</para>
    ///
    /// <para>A repo is cloned ONCE: a related repo whose <see cref="WorkspaceRepositorySpec.RepositoryId"/> duplicates
    /// the primary (an operator double-pick) or an earlier related entry is DROPPED — the primary (writable) and the
    /// first authored occurrence win. Without this, the same repo would clone into two mount folders with conflicting
    /// access. Symmetric to the alias de-dup: a collision is collapsed, never a double-clone.</para>
    /// </summary>
    public static WorkspaceSpec? FromAuthoredRepos(Guid primaryRepositoryId, string? primaryRef, IReadOnlyList<WorkspaceRepositorySpec> relatedRepositories, bool primaryRefSoftFallback = false, WorkspaceCwdMode cwdMode = WorkspaceCwdMode.Auto, string? primaryPinnedSha = null)
    {
        if (relatedRepositories.Count == 0) return null;

        var primary = new WorkspaceRepositorySpec { Alias = DefaultAlias, RepositoryId = primaryRepositoryId, Ref = primaryRef, RefSoftFallback = primaryRefSoftFallback, PinnedSha = primaryPinnedSha, Path = DefaultAlias, Access = WorkspaceAccess.Write, IsPrimary = true };

        var taken = new HashSet<string>(StringComparer.Ordinal) { DefaultAlias };
        var takenRepoIds = new HashSet<Guid> { primaryRepositoryId };
        var repos = new List<WorkspaceRepositorySpec> { primary };

        foreach (var related in relatedRepositories)
        {
            // De-dup by repo id: the primary + the first authored occurrence win; a self/duplicate related repo is
            // dropped so a repo is never cloned twice into conflicting mounts.
            if (!takenRepoIds.Add(related.RepositoryId)) continue;

            var alias = NormalizeAlias(related.Alias, taken);
            taken.Add(alias);

            repos.Add(related with { Alias = alias, IsPrimary = false });
        }

        return new WorkspaceSpec { Repositories = repos, PrimaryAlias = DefaultAlias, CwdMode = cwdMode };
    }

    /// <summary>
    /// Give a related repo a UNIQUE + SAFE alias — its authored alias when it's a safe single segment AND not already
    /// taken, else the next free <c>repo-N</c>. Guarantees the returned alias is non-empty, free of path separators /
    /// <c>.</c> / <c>..</c>, and distinct from every prior alias — so <see cref="FromAuthoredRepos"/> can NEVER produce
    /// the duplicate or traversing mount the provider's mount-layout validation would refuse at clone time (that
    /// validation stays as defence-in-depth). The fallback loops past <c>taken</c>, so a generated <c>repo-N</c> can't
    /// collide with an authored <c>repo-N</c> either.
    /// </summary>
    private static string NormalizeAlias(string? authored, HashSet<string> taken)
    {
        var candidate = (authored ?? "").Trim();

        if (candidate.Length > 0 && IsSafeAliasSegment(candidate) && !taken.Contains(candidate)) return candidate;

        var n = 2;
        while (taken.Contains($"repo-{n}")) n++;
        return $"repo-{n}";
    }

    /// <summary>A safe alias is a single directory NAME — not <c>.</c>/<c>..</c> and free of path separators — so it can never traverse outside the workspace root when used as a mount segment. (Mirrors the provider's stricter <c>IsSafeMountSegment</c>, kept here in Messages so the factory only ever emits safe aliases.)</summary>
    private static bool IsSafeAliasSegment(string segment) =>
        segment != "." && segment != ".." && segment.IndexOf('/') < 0 && segment.IndexOf('\\') < 0;

    /// <summary>The resolved primary repo: the <see cref="PrimaryAlias"/> match, else the explicit <see cref="WorkspaceRepositorySpec.IsPrimary"/>, else the first writable, else the first. Null only when <see cref="Repositories"/> is empty (an invalid spec).</summary>
    public WorkspaceRepositorySpec? Primary =>
        (PrimaryAlias is { } alias ? Repositories.FirstOrDefault(r => r.Alias == alias) : null)
        ?? Repositories.FirstOrDefault(r => r.IsPrimary)
        ?? Repositories.FirstOrDefault(r => r.Access == WorkspaceAccess.Write)
        ?? Repositories.FirstOrDefault();
}

/// <summary>One repository in a <see cref="WorkspaceSpec"/> (Rule 18.1 noun): which repo, at which ref, mounted at which path, with what access.</summary>
public sealed record WorkspaceRepositorySpec
{
    /// <summary>The short name the agent + outputs refer to this repo by (e.g. "web", "api"). Unique within a workspace. Also the default mount path.</summary>
    public required string Alias { get; init; }

    /// <summary>The bound repository to clone (team-scoped; resolved to a clone URL + token by the workspace resolver).</summary>
    public required Guid RepositoryId { get; init; }

    /// <summary>The branch/ref to check out. Null → the repository's default branch.</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// S1 — the EXACT commit this workspace materializes at. When set, the working tree is hard-checked-out at THIS
    /// sha after cloning <see cref="Ref"/>'s branch context (the clone deepens past its shallow default only when
    /// the tip advanced beyond the pin); a missing/unreachable pin fails the provision LOUD (never a silent tip
    /// fallback — the pin exists so the planner, the reviewers, and every parallel agent see the SAME immutable
    /// base, and a pin that cannot be honoured is a freshness violation, not a suggestion). Null (the default) →
    /// the tip of <see cref="Ref"/>, byte-identical to before this field existed (null-omitted from the serialized spec).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PinnedSha { get; init; }

    /// <summary>
    /// True when <see cref="Ref"/> is a SOFT, session-inherited prior branch — set ONLY by the session-continuity
    /// projection (never by an authored spec). When true and the prior branch was pruned on the remote, the resolver
    /// carries the default branch as the clone fallback (the run survives a merged-PR-deleted branch). False (the
    /// default) → <see cref="Ref"/> is HARD: an authored ref / a grader's produced-branch ref is used verbatim and the
    /// clone fails loud if it is gone — never silently rewritten. Byte-identical to before whenever false.
    /// <para>Omitted from the serialized spec when false (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>) so a
    /// non-session spec's persisted <c>task_json</c> is byte-identical to before this field existed.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RefSoftFallback { get; init; }

    /// <summary>The subdirectory under the workspace root this repo clones into in a MULTI-repo workspace (defaults to <see cref="Alias"/> when null). Ignored for a single-repo workspace (which clones flat at the repo root).</summary>
    public string? Path { get; init; }

    /// <summary>Whether the agent may WRITE this repo or only read it as context. Defaults to <see cref="WorkspaceAccess.Write"/> (the legacy single-repo behaviour).</summary>
    public WorkspaceAccess Access { get; init; } = WorkspaceAccess.Write;

    /// <summary>Marks the primary repo explicitly (the cwd target + compat-result source). At most one per workspace; <see cref="WorkspaceSpec.Primary"/> falls back when none is set.</summary>
    public bool IsPrimary { get; init; }
}

/// <summary>Where the harness's working directory points in a prepared workspace.</summary>
public enum WorkspaceCwdMode
{
    /// <summary>Repo-root for a single-repo workspace (the invariant), workspace-root for a multi-repo one. The default.</summary>
    Auto = 0,

    /// <summary>Always the shared workspace root (the agent sees every repo as a sibling folder).</summary>
    WorkspaceRoot = 1,

    /// <summary>Always the primary repo's root (even in a multi-repo workspace — the agent starts inside the primary, reaches siblings by relative path).</summary>
    PrimaryRepo = 2,
}

/// <summary>
/// Maps the wire vocabulary (the launch modal's <c>"auto"/"workspace"/"primary"</c>, or a <see cref="WorkspaceCwdMode"/>
/// enum name like <c>"WorkspaceRoot"</c>) to the enum. Returns NULL for <c>"auto"</c> / blank / unknown so the
/// <see cref="WorkspaceCwdMode.Auto"/> default is OMITTED end-to-end (an unset working-dir keeps every projection +
/// the persisted task_json byte-identical). Consumers fold a null back to <see cref="WorkspaceCwdMode.Auto"/>.
/// </summary>
public static class WorkspaceCwdModeWire
{
    public static WorkspaceCwdMode? FromWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "workspace" or "workspaceroot" => WorkspaceCwdMode.WorkspaceRoot,
        "primary" or "primaryrepo" => WorkspaceCwdMode.PrimaryRepo,
        _ => null,
    };
}

/// <summary>Whether an agent may write a workspace repo or only read it as context.</summary>
public enum WorkspaceAccess
{
    /// <summary>Read-only context — the agent may read the repo but its changes there are not captured/pushed.</summary>
    Read = 0,

    /// <summary>Writable — the agent's changes are captured + (optionally) pushed as a branch.</summary>
    Write = 1,
}
