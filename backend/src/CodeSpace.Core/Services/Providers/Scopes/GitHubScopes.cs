namespace CodeSpace.Core.Services.Providers.Scopes;

/// <summary>
/// Canonical GitHub OAuth scope names. Centralised so handlers, provider modules, tests, and
/// the wire-format response to the frontend all read the same string — no magic strings, no
/// typos. Names match GitHub's documented values verbatim: https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps
///
/// Add a new constant ONLY when we actually consume the scope. Hoarding every documented
/// scope here would tempt future code to request scopes we don't use, which inflates the
/// consent screen and erodes user trust.
/// </summary>
public static class GitHubScopes
{
    /// <summary>Full control of private + public repositories: read code, list, manage hooks.</summary>
    public const string Repo = "repo";

    /// <summary>Read-only on public repositories. Sufficient for listing public repos.</summary>
    public const string PublicRepo = "public_repo";

    /// <summary>Repository hook management (subset of repo). GitHub treats `repo` as a superset.</summary>
    public const string AdminRepoHook = "admin:repo_hook";

    /// <summary>Read profile info — needed for ProbeCredential to identify the user.</summary>
    public const string ReadUser = "read:user";
}
