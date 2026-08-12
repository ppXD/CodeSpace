using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Invitations;

/// <summary>Pending invitations for the current team. Never carries a token.</summary>
public sealed record ListTeamInvitationsQuery : IQuery<IReadOnlyList<TeamInvitationSummary>>, IRequireTeamMembership;

/// <summary>
/// What a token is worth, to whoever holds it. ANONYMOUS — see <c>AcceptInvitationCommand</c>.
/// Answers only for a token that checks out, so an invalid guess learns nothing about the team.
/// </summary>
public sealed record PreviewInvitationQuery : IQuery<InvitationPreview>
{
    /// <summary>Route-supplied. Nullable for the same reason as <c>AcceptInvitationCommand.Token</c>.</summary>
    public string? Token { get; init; }
}
