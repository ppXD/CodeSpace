namespace CodeSpace.Messages.Agents;

/// <summary>
/// A declarative request to materialise an isolated working copy of a repository for an agent run.
/// Provider-neutral: <see cref="Token"/> + <see cref="TokenUsername"/> are resolved upstream (per
/// provider — GitHub uses "x-access-token", GitLab "oauth2"), so the workspace provider only builds the
/// authenticated URL and clones. The same shape feeds a future in-pod (K8s) provider unchanged.
/// </summary>
public sealed record WorkspaceRequest
{
    /// <summary>HTTPS (or file://) clone URL of the repository.</summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>Branch / tag / sha to check out. Null → the remote's default branch.</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// The branch to fall back to when <see cref="Ref"/> is a SOFT, non-default ref that no longer exists on the remote
    /// — set only for a SESSION-inherited prior branch (which is transient: a merged PR auto-deletes it). When set and
    /// <see cref="Ref"/> is gone, the provider clones <see cref="DefaultRef"/> instead of failing the run. Null (the
    /// default) → <see cref="Ref"/> is HARD: used verbatim, the clone fails loud if it is gone (an authored ref's
    /// explicit intent is never silently rewritten). Byte-identical to before whenever null.
    /// </summary>
    public string? DefaultRef { get; init; }

    /// <summary>Access token for HTTPS auth. Null → an anonymous clone (public or local repo).</summary>
    public string? Token { get; init; }

    /// <summary>Basic-auth username paired with <see cref="Token"/> — provider-specific ("x-access-token", "oauth2"). Ignored when <see cref="Token"/> is null; defaults to "x-access-token".</summary>
    public string? TokenUsername { get; init; }

    /// <summary>Shallow-clone depth. 1 (default) fetches only the tip; 0 → a full clone. With <see cref="PinnedSha"/> set the clone STARTS at this depth and only deepens (fetch-by-sha, then unshallow) when the pin is not the fetched tip — the common pin-equals-tip launch stays shallow.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>S1 — the EXACT commit to materialize after cloning (see <c>WorkspaceRepositorySpec.PinnedSha</c>): a hard detached checkout of this sha, deepening the clone only when the tip advanced past the pin; fails LOUD when the pin is missing/unreachable. Null → the tip of <see cref="Ref"/> (byte-identical legacy behaviour).</summary>
    public string? PinnedSha { get; init; }
}
