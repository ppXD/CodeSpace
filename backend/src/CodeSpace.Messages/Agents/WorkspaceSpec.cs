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
    /// <see cref="WorkspaceSpec"/> as a multi-repo one, with byte-identical execution.
    /// </summary>
    public static WorkspaceSpec FromRepository(Guid repositoryId, string? @ref = null) => new()
    {
        Repositories = new[]
        {
            new WorkspaceRepositorySpec { Alias = DefaultAlias, RepositoryId = repositoryId, Ref = @ref, Path = DefaultAlias, Access = WorkspaceAccess.Write, IsPrimary = true },
        },
        PrimaryAlias = DefaultAlias,
        CwdMode = WorkspaceCwdMode.Auto,
    };

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

/// <summary>Whether an agent may write a workspace repo or only read it as context.</summary>
public enum WorkspaceAccess
{
    /// <summary>Read-only context — the agent may read the repo but its changes there are not captured/pushed.</summary>
    Read = 0,

    /// <summary>Writable — the agent's changes are captured + (optionally) pushed as a branch.</summary>
    Write = 1,
}
