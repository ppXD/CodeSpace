namespace CodeSpace.Messages.Agents;

/// <summary>
/// The RESOLVED, ready-to-clone multi-repo provision plan (multi-repo PR2) — what the
/// <c>IWorkspaceProvider</c> materialises: one or many repositories, each a concrete <see cref="WorkspaceRequest"/>
/// clone instruction (URL + ref + token), plus where the harness's cwd points. The generic底座 the single-repo
/// case is the 1-element special case of (Rule 18.1 noun).
///
/// <para>Distinct from <c>WorkspaceSpec</c> (the AUTHORED intent — which repos by id + access): the resolver turns
/// a spec into THIS by resolving each repo's clone URL + short-lived token. A single-repo run is
/// <see cref="FromSingle"/> — one writable primary repo — so it clones + runs byte-identically to before.</para>
///
/// <para>SINGLE-REPO RUNTIME INVARIANT (preserved): a one-repo provision clones directly into the workspace root
/// and runs the harness there; only a genuine multi-repo provision (&gt;1) clones each repo into a
/// <c>&lt;root&gt;/&lt;path&gt;</c> subdir and (under <see cref="WorkspaceCwdMode.Auto"/>) points cwd at the root.</para>
/// </summary>
public sealed record WorkspaceProvisionRequest
{
    /// <summary>The repositories to clone, in order. Always at least one; a single-element list is the common (legacy single-repo) case.</summary>
    public required IReadOnlyList<WorkspaceRepositoryProvision> Repositories { get; init; }

    /// <summary>The alias of the PRIMARY repo (cwd target under <see cref="WorkspaceCwdMode.PrimaryRepo"/> / single-repo, and the source of the legacy compat capture/push). Null → derived (see <see cref="Primary"/>).</summary>
    public string? PrimaryAlias { get; init; }

    /// <summary>Where the harness runs. <see cref="WorkspaceCwdMode.Auto"/> (default) = repo-root for one repo, workspace-root for many.</summary>
    public WorkspaceCwdMode CwdMode { get; init; } = WorkspaceCwdMode.Auto;

    /// <summary>The single-repo provision an existing run implies — one writable primary repo at the default alias, cwd Auto. The back-compat bridge so a single-repo run provisions through the SAME path as a multi-repo one, byte-identically.</summary>
    public static WorkspaceProvisionRequest FromSingle(WorkspaceRequest clone) => new()
    {
        Repositories = new[] { new WorkspaceRepositoryProvision { Alias = WorkspaceSpec.DefaultAlias, CloneRequest = clone, Path = WorkspaceSpec.DefaultAlias, Access = WorkspaceAccess.Write, IsPrimary = true } },
        PrimaryAlias = WorkspaceSpec.DefaultAlias,
        CwdMode = WorkspaceCwdMode.Auto,
    };

    /// <summary>The resolved primary repo: the <see cref="PrimaryAlias"/> match, else the explicit <see cref="WorkspaceRepositoryProvision.IsPrimary"/>, else the first writable, else the first. Mirrors <c>WorkspaceSpec.Primary</c>. Null only when <see cref="Repositories"/> is empty.</summary>
    public WorkspaceRepositoryProvision? Primary =>
        (PrimaryAlias is { } alias ? Repositories.FirstOrDefault(r => r.Alias == alias) : null)
        ?? Repositories.FirstOrDefault(r => r.IsPrimary)
        ?? Repositories.FirstOrDefault(r => r.Access == WorkspaceAccess.Write)
        ?? Repositories.FirstOrDefault();
}

/// <summary>One repository to clone in a <see cref="WorkspaceProvisionRequest"/> (Rule 18.1 noun): the alias, the concrete clone instruction, the mount path, and the access.</summary>
public sealed record WorkspaceRepositoryProvision
{
    /// <summary>The short name the agent + outputs refer to this repo by; also the default mount subdir. Unique within the provision.</summary>
    public required string Alias { get; init; }

    /// <summary>The concrete clone instruction (URL + ref + token + depth) — the resolved per-repo <see cref="WorkspaceRequest"/>. (Named <c>CloneRequest</c> not <c>Clone</c> — the latter is reserved on records.)</summary>
    public required WorkspaceRequest CloneRequest { get; init; }

    /// <summary>The subdirectory under the workspace root this repo clones into in a MULTI-repo provision (defaults to <see cref="Alias"/> when null). Ignored for a single-repo provision (which clones flat at the root).</summary>
    public string? Path { get; init; }

    /// <summary>Whether the agent may WRITE this repo or only read it as context. Defaults to <see cref="WorkspaceAccess.Write"/> (the legacy single-repo behaviour).</summary>
    public WorkspaceAccess Access { get; init; } = WorkspaceAccess.Write;

    /// <summary>Marks the primary repo explicitly (the cwd target + legacy compat capture/push source).</summary>
    public bool IsPrimary { get; init; }
}
