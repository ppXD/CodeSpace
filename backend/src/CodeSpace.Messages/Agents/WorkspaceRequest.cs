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

    /// <summary>Access token for HTTPS auth. Null → an anonymous clone (public or local repo).</summary>
    public string? Token { get; init; }

    /// <summary>Basic-auth username paired with <see cref="Token"/> — provider-specific ("x-access-token", "oauth2"). Ignored when <see cref="Token"/> is null; defaults to "x-access-token".</summary>
    public string? TokenUsername { get; init; }

    /// <summary>Shallow-clone depth. 1 (default) fetches only the tip; 0 → a full clone.</summary>
    public int Depth { get; init; } = 1;
}
