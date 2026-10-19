using System.Text.Json.Serialization;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>
/// What is wrong with a provider connection and what the CALLER can do about it — the answer a run page, a repository row and a
/// refused webhook delivery all give instead of a sentence nobody can act on. Written in the caller's terms: the same failure
/// reads one way to a team Owner (every button works), another to a Member (the credential's owner's own buttons work, the rest
/// name who to ask) and another to a Viewer (nothing works; here are the people who can). The words are the backend's; a client
/// renders them and offers the actions it is told are enabled.
///
/// <para>Computed at read time from the failure that was written down and from what is true NOW — a credential reconnected since
/// the run failed reads <see cref="Resolved"/>, so the page offers the re-run instead of the repair. It is therefore never cached
/// with the page that shows it: the room's cached turn carries only the failure's code, and the card asks for this.</para>
/// </summary>
public sealed record ProviderRemedy
{
    /// <summary>The failure's stable code (<c>credential_disconnected</c>, <c>provider_base_url_redirects</c>, …) — what a client keys its icon on, never the words.</summary>
    public required string Code { get; init; }

    /// <summary>One line naming what is wrong, in plain language.</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences of cause that name the credential and the repository — the specifics the title leaves out.</summary>
    public required string Cause { get; init; }

    /// <summary>True when what the failure names works again — the credential is live, the repository is bound to a live one, the address no longer redirects — so the repair is done and the run only needs another go.</summary>
    public required bool Resolved { get; init; }

    /// <summary>The credential the failure is about; null when the failure names none and none could be traced (a provider's 401 on a step with no repository).</summary>
    public RemedyCredential? Credential { get; init; }

    /// <summary>The repositories the failing step acted on, most affected first, capped; empty for a failure that is about no repository.</summary>
    public IReadOnlyList<RemedyRepository> Repositories { get; init; } = Array.Empty<RemedyRepository>();

    /// <summary>The provider connection whose address redirects, or the instance of the credential; null when none could be named.</summary>
    public RemedyProviderInstance? ProviderInstance { get; init; }

    /// <summary>For <c>provider_base_url_redirects</c>: the origin the provider answered with, what its base URL would be edited to. Null for every other failure.</summary>
    public string? RedirectsTo { get; init; }

    /// <summary>What can be done, best first, each saying whether the caller may do it and, when not, why. Never empty.</summary>
    public required IReadOnlyList<RemedyAction> Actions { get; init; }

    /// <summary>Who to ask when some action is not the caller's: the team's Owners and Admins, and the credential's own person for a token replace only they can make. Empty when every action is the caller's, or nobody can be named.</summary>
    public IReadOnlyList<RemedyPerson> AskPeople { get; init; } = Array.Empty<RemedyPerson>();
}

/// <summary>The kinds of fix the remedy offers. Serialized as the lower-case names a client branches on, so a rename is a breaking change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemedyActionKind>))]
public enum RemedyActionKind
{
    /// <summary>Move the repository (or every repository the credential holds) onto a live credential.</summary>
    [JsonStringEnumMemberName("relink")] Relink,

    /// <summary>Sign in again with the provider, into the SAME credential, so nothing bound through it changes. The credential's own person, or <c>credentials.manage</c>: starting a connect that names the credential is held to the same bar as replacing its token.</summary>
    [JsonStringEnumMemberName("reconnect")] Reconnect,

    /// <summary>Paste a new token into the SAME credential, so nothing bound through it changes. The credential's own person, or <c>credentials.manage</c>.</summary>
    [JsonStringEnumMemberName("replace_token")] ReplaceToken,

    /// <summary>Correct the provider connection's address to the one the host answers on.</summary>
    [JsonStringEnumMemberName("edit_provider_url")] EditProviderUrl,

    /// <summary>Open the Providers dialog — the one place every connection can be seen and repaired.</summary>
    [JsonStringEnumMemberName("open_providers")] OpenProviders,

    /// <summary>Run it again; offered once what failed is <see cref="ProviderRemedy.Resolved"/>.</summary>
    [JsonStringEnumMemberName("rerun")] Rerun
}

public sealed record RemedyAction
{
    public required RemedyActionKind Kind { get; init; }
    public required string Label { get; init; }

    /// <summary>False when the caller may not do this; <see cref="DisabledReason"/> says what they would need. The button is shown disabled with the reason, never hidden: a person who cannot fix it must still learn that it is fixable and by whom.</summary>
    public required bool Enabled { get; init; }

    public string? DisabledReason { get; init; }
}

/// <summary>The credential a remedy is about — enough to name it, to say whose it is, and to tell what a repair would be.</summary>
public sealed record RemedyCredential
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required CredentialOwnership Ownership { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string? OwnerName { get; init; }
    public required AuthType AuthType { get; init; }
    public required CredentialStatus Status { get; init; }

    /// <summary>What to read off it now: working, expiring, refused, disconnected, or lent by someone who left.</summary>
    public required CredentialHealth Health { get; init; }

    /// <summary>Soft-deleted: its person left the team or its connection was removed. It cannot be reconnected; what leans on it can only be moved.</summary>
    public required bool Retired { get; init; }

    public required Guid ProviderInstanceId { get; init; }
}

/// <summary>A repository the failing step acted on, and whether what it runs through works now.</summary>
public sealed record RemedyRepository
{
    public required Guid Id { get; init; }
    public required string FullPath { get; init; }
    public required RepositoryStatus Status { get; init; }

    /// <summary>The credential the repository runs through now; null when it has none, or the one it had is gone.</summary>
    public Guid? CredentialId { get; init; }

    /// <summary>The repository authenticates through a credential that works: bound, live, not refused, not retired.</summary>
    public required bool CredentialWorks { get; init; }
}

public sealed record RemedyProviderInstance
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required ProviderKind Provider { get; init; }
    public required string BaseUrl { get; init; }
}

public sealed record RemedyPerson
{
    public required Guid UserId { get; init; }
    public required string Name { get; init; }

    /// <summary>Why this person: <c>Owner</c> or <c>Admin</c> of the team, or <c>Owner of the credential</c>.</summary>
    public required string Role { get; init; }
}
