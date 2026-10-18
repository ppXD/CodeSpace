using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Which credentials may take a credential's bindings, and in what order they are offered. Pure, so the rules and the order
/// are pinned without a database — and so the dialog that offers a target and the door that accepts it read one rule.
///
/// <para>Two doors, two rules, chosen by whether the credential is still live. A <b>dead</b> credential's bindings are moved by
/// a team Admin with a relink: onto the caller's own live connections first — newest first, because the connection a person
/// just made is the one they are most likely replacing the dead one with — then the team's service tokens, newest first; a
/// teammate's personal token is never offered, since the repositories would act as that person. A <b>live</b> credential's
/// bindings move only when it is disconnected, and then only to another connection of the SAME owner (for a team token,
/// another team token): whoever asks, the repositories keep acting as the person — or the team — they acted as.</para>
/// </summary>
internal static class RelinkSuggestionRanking
{
    public static IReadOnlyList<RelinkSuggestion> Rank(Credential from, IEnumerable<Credential> candidates, Guid callerId) =>
        candidates
            .Where(c => Qualifies(from, c, callerId))
            .Select(c => (Credential: c, Kind: KindOf(c, callerId)))
            .OrderBy(offer => offer.Kind)
            .ThenByDescending(offer => offer.Credential.CreatedDate)
            .ThenBy(offer => offer.Credential.Id)
            .Select(offer => Suggest(from, offer.Credential, offer.Kind))
            .ToList();

    /// <summary>
    /// Live, on the credential's team and instance, not the credential itself, and what the door that would move the
    /// bindings accepts: for a live credential <see cref="HaveTheSameOwner"/> (a Disconnect's rule), for a dead one
    /// <see cref="IsCallersOrTeams"/> (a relink's). The standing of a person's credential's holder is the one thing read from
    /// the database, by <see cref="ICredentialSuccessionService.RefusalAsync"/>.
    /// </summary>
    public static bool Qualifies(Credential from, Credential candidate, Guid callerId) =>
        candidate.Id != from.Id
        && candidate.TeamId == from.TeamId
        && candidate.ProviderInstanceId == from.ProviderInstanceId
        && IsLive(candidate)
        && (IsLive(from) ? HaveTheSameOwner(from, candidate) : IsCallersOrTeams(candidate, callerId));

    /// <summary>Connected and not retired: the credential can still authenticate, so moving its bindings is a Disconnect that says where they go.</summary>
    public static bool IsLive(Credential credential) => credential.Status == CredentialStatus.Active && credential.DeletedDate == null;

    /// <summary>A Disconnect's rule: the same person's — or, for a team token, the team's — never anyone else's. Compared as owner and ownership, so a team token matches only another team token.</summary>
    public static bool HaveTheSameOwner(Credential a, Credential b) => a.OwnerUserId == b.OwnerUserId && a.Ownership == b.Ownership;

    /// <summary>A relink's rule: a person's own connection, or a team-service token — never another person's personal one.</summary>
    public static bool IsCallersOrTeams(Credential candidate, Guid callerId) => IsCallers(candidate, callerId) || IsTeamService(candidate);

    private static RelinkSuggestionKind KindOf(Credential c, Guid callerId) =>
        IsCallers(c, callerId) ? RelinkSuggestionKind.YourConnection : IsTeamService(c) ? RelinkSuggestionKind.TeamServiceToken : RelinkSuggestionKind.OwnersOtherConnection;

    private static bool IsCallers(Credential c, Guid callerId) => c.Ownership == CredentialOwnership.Personal && c.OwnerUserId == callerId;

    private static bool IsTeamService(Credential c) => c.Ownership == CredentialOwnership.TeamService;

    private static RelinkSuggestion Suggest(Credential from, Credential c, RelinkSuggestionKind kind) => new()
    {
        CredentialId = c.Id,
        DisplayName = c.DisplayName,
        AuthType = c.AuthType,
        Ownership = c.Ownership,
        Kind = kind,
        CreatedDate = c.CreatedDate,
        ConnectedBeforeDisconnect = from.Status != CredentialStatus.Active && c.CreatedDate < from.LastModifiedDate
    };
}
