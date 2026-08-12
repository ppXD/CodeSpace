using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Invitations;

/// <summary>
/// The invitation lifecycle. Team scope comes from <c>ICurrentTeam</c> on the management calls and
/// from the token itself on the two anonymous ones — never from a caller-supplied team id.
/// </summary>
public interface ITeamInvitationService
{
    Task<CreateInvitationResult> InviteAsync(string email, TeamRole role, CancellationToken cancellationToken);
    Task<IReadOnlyList<TeamInvitationSummary>> ListAsync(CancellationToken cancellationToken);
    Task RevokeAsync(Guid invitationId, CancellationToken cancellationToken);
    Task<CreateInvitationResult> RegenerateAsync(Guid invitationId, CancellationToken cancellationToken);

    /// <summary>Anonymous: the token is the authorization.</summary>
    Task<InvitationPreview> PreviewAsync(string token, CancellationToken cancellationToken);

    /// <summary>Anonymous: spends the invitation and mints the session the invitee signs in with.</summary>
    Task<SignInResponse> AcceptAsync(string token, string? name, string? password, CancellationToken cancellationToken);
}
