using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

/// <summary>
/// The credentials the caller could point a repository on this provider connection at, best first — the whole list a Re-link
/// dialog shows, computed here so the dialog filters nothing itself. Every credential that can authenticate — connected, and for a
/// static token not past its recorded expiry, as the credential list reads it — that the caller may bind with, their own
/// or the team's service tokens (never a teammate's personal one, whose repositories would act as that person), on this
/// connection or on any other connection of the team to the same host (<c>ProviderInstanceHost</c>): the same connection's first,
/// then the caller's own, then the team's, each newest first. A candidate on another connection moves the repository to that
/// connection when it is picked, and names the teammates whose act-as that strands (<c>StrandedActAs</c>). <see cref="FromCredentialId"/>
/// is the credential the repository is leaving, when it has one: it is left out of the list, and a candidate connected before it was
/// disconnected is flagged.
///
/// <para>Membership alone, like <see cref="GetRelinkSuggestionsQuery"/>. The connection and the credential are read by (id, header
/// team), so another team's id reads as not found; <see cref="FromCredentialId"/> may be soft-deleted, a departed member's retired
/// token being the case that most needs this.</para>
/// </summary>
public sealed record GetRelinkCandidatesQuery : IQuery<IReadOnlyList<RelinkSuggestion>>, IRequireTeamMembership
{
    /// <summary>Set by the controller from the route segment via <c>query with { ProviderInstanceId = ... }</c>.</summary>
    public Guid ProviderInstanceId { get; init; }

    public Guid? FromCredentialId { get; init; }
}
