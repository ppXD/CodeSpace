using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>Whose connection a suggestion is. Declared best-first: suggestions are ordered by it, then newest first.</summary>
public enum RelinkSuggestionKind
{
    /// <summary>A live Personal credential of the person asking.</summary>
    YourConnection,

    /// <summary>A live team-service token — the team's, owned by no person, so it survives anyone leaving.</summary>
    TeamServiceToken,

    /// <summary>Another live Personal credential of the person the credential belongs to, offered to someone else asking about a credential that is still connected: a Disconnect moves its repositories only onto the same owner's other connections.</summary>
    OwnersOtherConnection
}

/// <summary>
/// A credential the caller could move a credential's bindings onto. Only candidates the move would accept are offered. For a
/// credential that is gone: live, on the same provider instance — or on another connection to the same host — and either the
/// caller's own or the team's, never a teammate's personal token, because binding repositories to it would make them act as that
/// person. For one still connected, which only a Disconnect that names the target can move: another connection of the same owner.
/// Offered the same connection's credentials first, so a candidate whose <see cref="ProviderInstanceId"/> differs from the
/// repository's is one that also moves the repository to that connection.
/// </summary>
public sealed record RelinkSuggestion
{
    public required Guid CredentialId { get; init; }
    public required string DisplayName { get; init; }
    public required AuthType AuthType { get; init; }
    public required CredentialOwnership Ownership { get; init; }
    public required RelinkSuggestionKind Kind { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>The provider connection the credential belongs to: the credential's own, or another connection to the same host — where a repository lives after a move onto it.</summary>
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>That connection's name, for a person to tell two connections to one host apart; empty when it was not read.</summary>
    public required string ProviderInstanceName { get; init; }

    /// <summary>
    /// Names of the teammates whose act-as stops working if this credential takes the repository: they have a live identity on the connection
    /// the repository is leaving (<c>ListCandidatesAsync</c> offers them as authors today) and none on this credential's connection, and an
    /// identity belongs to a connection, so a run that acts as them on the repository asks them to connect there first. Empty for a credential on
    /// the repository's own connection — nothing about where it lives changes — and for any candidate whose connection already has them all.
    /// </summary>
    public IReadOnlyList<string> StrandedActAs { get; init; } = Array.Empty<string>();

    /// <summary>The candidate was connected before the credential it would replace was disconnected — the connect-then-disconnect shape, where nothing but a person's choice moves the repositories.</summary>
    public required bool ConnectedBeforeDisconnect { get; init; }
}
