namespace CodeSpace.Core.Services.Providers.Scopes;

/// <summary>
/// Canonical GitLab OAuth scope names. Centralised so handlers, provider modules, tests, and
/// the wire-format response to the frontend all read the same string — no magic strings, no
/// typos. Names match GitLab's documented values verbatim: https://docs.gitlab.com/ee/integration/oauth_provider.html#authorized-applications
///
/// GitLab's scope model is coarser than GitHub's: <c>api</c> is the universal grant for
/// API + repo + webhook + admin, while <c>read_api</c> / <c>read_repository</c> are
/// read-only subsets that are NOT sufficient for webhook registration.
/// </summary>
public static class GitLabScopes
{
    /// <summary>Full API access — read + write repos, manage webhooks, admin projects.</summary>
    public const string Api = "api";

    /// <summary>Read-only API access — no writes, no webhook registration.</summary>
    public const string ReadApi = "read_api";

    /// <summary>Read repository files + clone via HTTPS. NOT sufficient to call /projects API.</summary>
    public const string ReadRepository = "read_repository";

    /// <summary>Write repository (push). Not needed for catalog/webhook today.</summary>
    public const string WriteRepository = "write_repository";

    /// <summary>Read user profile — needed for ProbeCredential to identify the user.</summary>
    public const string ReadUser = "read_user";
}
