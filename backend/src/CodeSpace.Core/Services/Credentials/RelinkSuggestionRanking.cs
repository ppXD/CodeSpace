using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Core.Services.ProviderInstances;
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
///
/// <para>"On the credential's instance" reaches further than the instance itself: a credential on another connection to the
/// same host (<see cref="ProviderInstanceHost"/>) takes bindings as one on the same connection does, and is offered after
/// the same connection's, whose credentials move a repository without changing which connection it lives under. The caller names the
/// connections in reach (<c>hostInstanceIds</c>); without them only the credential's own instance is.</para>
/// </summary>
internal static class RelinkSuggestionRanking
{
    public static IReadOnlyList<RelinkSuggestion> Rank(Credential from, IEnumerable<Credential> candidates, Guid callerId, IReadOnlySet<Guid>? hostInstanceIds = null) =>
        Order(from.ProviderInstanceId, candidates.Where(c => Qualifies(from, c, callerId, hostInstanceIds)), callerId)
            .Select(offer => Suggest(from, offer.Credential, offer.Kind))
            .ToList();

    /// <summary>
    /// What a Re-link dialog offers for a repository on <paramref name="providerInstanceId"/>: every credential among
    /// <paramref name="candidates"/> that can authenticate at <paramref name="now"/> (<see cref="CanAuthenticate"/>) and the caller may
    /// bind with — their own, or the team's service tokens, never a teammate's — other than the one the repository is leaving. Unlike
    /// <see cref="Rank"/> it does not depend on whether that credential is still live: a repository whose credential merely looks fine is
    /// moved off it as readily as a dead one's. The candidates are the caller's to have loaded already, from the connections in reach.
    /// </summary>
    public static IReadOnlyList<RelinkSuggestion> RankForRepository(Guid providerInstanceId, Credential? leaving, IEnumerable<Credential> candidates, Guid callerId, DateTimeOffset now) =>
        Order(providerInstanceId, candidates.Where(c => c.Id != leaving?.Id && CanAuthenticate(c, now) && IsCallersOrTeams(c, callerId)), callerId)
            .Select(offer => Suggest(leaving, offer.Credential, offer.Kind))
            .ToList();

    /// <summary>The same connection's credentials first — nothing about the repository's connection changes — then the caller's own, then the team's, each newest first.</summary>
    private static IEnumerable<(Credential Credential, RelinkSuggestionKind Kind)> Order(Guid providerInstanceId, IEnumerable<Credential> offered, Guid callerId) =>
        offered
            .Select(c => (Credential: c, Kind: KindOf(c, callerId)))
            .OrderBy(offer => offer.Credential.ProviderInstanceId == providerInstanceId ? 0 : 1)
            .ThenBy(offer => offer.Kind)
            .ThenByDescending(offer => offer.Credential.CreatedDate)
            .ThenBy(offer => offer.Credential.Id);

    /// <summary>
    /// Live, on the credential's team and instance (or one of <paramref name="hostInstanceIds"/>, the connections to the same host),
    /// not the credential itself, and what the door that would move the bindings accepts: for a live credential
    /// <see cref="HaveTheSameOwner"/> (a Disconnect's rule), for a dead one <see cref="IsCallersOrTeams"/> (a relink's). The standing
    /// of a person's credential's holder is the one thing read from the database, by <see cref="ICredentialSuccessionService.RefusalAsync"/>.
    /// </summary>
    public static bool Qualifies(Credential from, Credential candidate, Guid callerId, IReadOnlySet<Guid>? hostInstanceIds = null) =>
        candidate.Id != from.Id
        && candidate.TeamId == from.TeamId
        && (candidate.ProviderInstanceId == from.ProviderInstanceId || (hostInstanceIds?.Contains(candidate.ProviderInstanceId) ?? false))
        && IsLive(candidate)
        && (IsLive(from) ? HaveTheSameOwner(from, candidate) : IsCallersOrTeams(candidate, callerId));

    /// <summary>Connected and not retired: the credential can still authenticate, so moving its bindings is a Disconnect that says where they go.</summary>
    public static bool IsLive(Credential credential) => credential.Status == CredentialStatus.Active && credential.DeletedDate == null;

    /// <summary>
    /// Connected, not retired and — what <see cref="IsLive"/> alone does not say — not a static token past its recorded expiry, which stays Active by status
    /// until a probe flips it while the credential list already reads it as rejected. <see cref="RemedyResolution.IsLive"/>, the definition the health chip and
    /// the remedy cards read, so what a person is offered to re-link to and what the list shows as working cannot disagree.
    /// </summary>
    public static bool CanAuthenticate(Credential credential, DateTimeOffset now) =>
        RemedyResolution.IsLive(new RemedyResolution.CredentialState(credential.Id, credential.Status, credential.AuthType, credential.ExpiresDate, credential.LastValidatedDate, Retired: credential.DeletedDate != null), now);

    /// <summary>A Disconnect's rule: the same person's — or, for a team token, the team's — never anyone else's. Compared as owner and ownership, so a team token matches only another team token.</summary>
    public static bool HaveTheSameOwner(Credential a, Credential b) => a.OwnerUserId == b.OwnerUserId && a.Ownership == b.Ownership;

    /// <summary>A relink's rule: a person's own connection, or a team-service token — never another person's personal one.</summary>
    public static bool IsCallersOrTeams(Credential candidate, Guid callerId) => IsCallers(candidate, callerId) || IsTeamService(candidate);

    private static RelinkSuggestionKind KindOf(Credential c, Guid callerId) =>
        IsCallers(c, callerId) ? RelinkSuggestionKind.YourConnection : IsTeamService(c) ? RelinkSuggestionKind.TeamServiceToken : RelinkSuggestionKind.OwnersOtherConnection;

    private static bool IsCallers(Credential c, Guid callerId) => c.Ownership == CredentialOwnership.Personal && c.OwnerUserId == callerId;

    private static bool IsTeamService(Credential c) => c.Ownership == CredentialOwnership.TeamService;

    private static RelinkSuggestion Suggest(Credential? from, Credential c, RelinkSuggestionKind kind) => new()
    {
        CredentialId = c.Id,
        DisplayName = c.DisplayName,
        AuthType = c.AuthType,
        Ownership = c.Ownership,
        Kind = kind,
        CreatedDate = c.CreatedDate,
        ProviderInstanceId = c.ProviderInstanceId,
        ProviderInstanceName = c.ProviderInstance?.DisplayName ?? string.Empty,
        ConnectedBeforeDisconnect = from is not null && from.Status != CredentialStatus.Active && c.CreatedDate < from.LastModifiedDate
    };
}
