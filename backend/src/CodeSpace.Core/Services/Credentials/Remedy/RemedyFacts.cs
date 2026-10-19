using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// The specifics a remedy's words and actions are built from, already read and already resolved to text — so the wording is a pure
/// function of them and a unit test can pin every code's sentence without a database. A fact that is not known is null, and the
/// sentence leaves it out rather than inventing it.
/// </summary>
public sealed record RemedyFacts
{
    /// <summary>The provider's name as a person says it: GitLab, GitHub, Git. Null when the failure did not say which, and the sentence says "the provider".</summary>
    public string? Provider { get; init; }

    public string? CredentialName { get; init; }

    /// <summary>The person the credential belongs to; null for a team-service credential.</summary>
    public string? OwnerName { get; init; }

    /// <summary>The credential is a person's own and the asker is that person.</summary>
    public bool CallerOwnsCredential { get; init; }

    /// <summary>The role the credential's person stands as in the team, for wording who outranks them; null for a team's own credential and for a person who left.</summary>
    public TeamRole? OwnerRole { get; init; }

    /// <summary>The credential is a person's own and that person no longer stands in the team: it is waiting to be retired, and nobody can put it right in place — only move what leans on it.</summary>
    public bool CredentialOwnerLeft { get; init; }

    /// <summary>Soft-deleted: it cannot be reconnected, only replaced by moving what leans on it.</summary>
    public bool CredentialRetired { get; init; }

    /// <summary>A token a person pasted (personal, project or group), as opposed to a sign-in. Decides Reconnect versus Replace token.</summary>
    public bool CredentialIsStaticToken { get; init; }

    /// <summary>Whether the credential is a team's own, owned by no person.</summary>
    public bool CredentialIsTeamService { get; init; }

    /// <summary>The repositories the failure was about, as a phrase: one path, two paths, or "3 repositories". Null when none could be named.</summary>
    public string? Repositories { get; init; }

    public int RepositoryCount { get; init; }

    /// <summary>The origin the provider's address redirected to, for a redirect failure.</summary>
    public string? RedirectsTo { get; init; }

    /// <summary>The origin the credential was connected for, for a redirect failure.</summary>
    public string? RequestedOrigin { get; init; }

    /// <summary>For a redirect: the repair the server would ACCEPT (<see cref="RemedyResolution.RepairRedirect"/>), which decides whether an edit button is offered and what the sentence says to do. Null when the provider connection could not be named.</summary>
    public RedirectRepair? RedirectFix { get; init; }

    /// <summary>For a redirect: how many credentials are connected through the provider — the ones an edit of its host would send somewhere new, and the reason the server refuses it.</summary>
    public int ConnectedCredentials { get; init; }

    /// <summary>The provider connection's own name, when one is known.</summary>
    public string? InstanceName { get; init; }

    /// <summary>What the provider answered (a status), when the failure carried it.</summary>
    public string? ProviderStatus { get; init; }

    /// <summary>For a renewal that failed: the provider's OAuth error code (<c>http_503</c>, <c>temporarily_unavailable</c>). Under a run a final refusal is <c>credential_needs_reconnect</c>, so this is always an answer that says nothing about the grant.</summary>
    public string? OAuthError { get; init; }

    /// <summary>For a refused trigger delivery that could not read an author's standing: which of <see cref="CodeSpace.Messages.Constants.StandingLookupCauses"/> failed. Null for a row written before it was kept, which the sentence then words as either.</summary>
    public string? Lookup { get; init; }

    /// <summary>What the failure names works now.</summary>
    public bool Resolved { get; init; }

    /// <summary>Whether a fix would show in what is recorded (<see cref="RemedyResolution.FixIsObservable"/>). When it would not, "run it again" is not held back waiting for proof that cannot appear.</summary>
    public bool FixIsObservable { get; init; } = true;
}

/// <summary>What can actually put a redirecting provider right, given what the server will let be edited.</summary>
public enum RedirectRepair
{
    /// <summary>Setting the base URL to where the provider points is accepted: nothing is connected through it, or the change is an http to https upgrade of the same host that connected tokens may follow.</summary>
    EditAddress,

    /// <summary>The address points at another host, scheme or port and credentials are connected, which the server refuses to move: they are disconnected first, or the new address is added as a provider of its own.</summary>
    DisconnectFirst,

    /// <summary>Nothing CodeSpace can edit: the redirect stays inside one origin (a request that changes something, redirects that do not end), or it named no address.</summary>
    HostSide
}
