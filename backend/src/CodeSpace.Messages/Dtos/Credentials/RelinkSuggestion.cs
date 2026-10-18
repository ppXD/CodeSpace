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
/// credential that is gone: live, on the same provider instance, and either the caller's own or the team's — never a
/// teammate's personal token, because binding repositories to it would make them act as that person. For one still
/// connected, which only a Disconnect that names the target can move: another connection of the same owner.
/// </summary>
public sealed record RelinkSuggestion
{
    public required Guid CredentialId { get; init; }
    public required string DisplayName { get; init; }
    public required AuthType AuthType { get; init; }
    public required CredentialOwnership Ownership { get; init; }
    public required RelinkSuggestionKind Kind { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>The candidate was connected before the credential it would replace was disconnected — the connect-then-disconnect shape, where nothing but a person's choice moves the repositories.</summary>
    public required bool ConnectedBeforeDisconnect { get; init; }
}
