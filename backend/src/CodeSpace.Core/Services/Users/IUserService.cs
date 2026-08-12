using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Dtos.Users;

namespace CodeSpace.Core.Services.Users;

/// <summary>
/// User identity surface — sign-in, change-password, and the /me query that drives the
/// SPA's auth state. Centralises the MeResponse projection that three handlers used to
/// duplicate (sign-in, change-password, the dedicated /me query).
/// </summary>
public interface IUserService
{
    Task<SignInResponse> SignInAsync(string nameOrEmail, string password, CancellationToken cancellationToken);
    Task<ChangePasswordResponse> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken);
    Task<MeResponse> GetMeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TeamMemberSummary>> ListTeamMembersAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The /me projection for a user OTHER than the request's principal — the one case being an
    /// invitation acceptance, which mints a session for an account that did not exist when the
    /// request began, so <see cref="GetMeAsync"/> (which reads ICurrentUser) cannot describe it.
    /// </summary>
    Task<MeResponse> BuildMeForAsync(User user, CancellationToken cancellationToken);
}
