using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Invitations;

/// <summary>
/// Pending invitations for the current team. Never carries a token.
///
/// <para>Gated on members.manage rather than plain membership: who has been offered a seat, at what
/// role, and by whom is management information. A Viewer reading it learns about people who are not
/// in the team yet, which is not part of what the read-only role exists to give them.</para>
/// </summary>
public sealed record ListTeamInvitationsQuery : IQuery<IReadOnlyList<TeamInvitationSummary>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.MembersManage;
}

/// <summary>
/// What a token is worth, to whoever holds it. ANONYMOUS — see <c>AcceptInvitationCommand</c>.
/// Answers only for a token that checks out, so an invalid guess learns nothing about the team.
/// </summary>
public sealed record PreviewInvitationQuery : IQuery<InvitationPreview>
{
    /// <summary>Route-supplied. Nullable for the same reason as <c>AcceptInvitationCommand.Token</c>.</summary>
    public string? Token { get; init; }
}
