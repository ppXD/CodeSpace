using CodeSpace.Messages.Dtos.Users;

namespace CodeSpace.Core.Services.Users;

/// <summary>Switching an account off, back on, and handing back a way in.</summary>
public interface IAccountLifecycleService
{
    Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken cancellationToken);
    Task DeactivateAsync(Guid userId, CancellationToken cancellationToken);
    Task ReactivateAsync(Guid userId, CancellationToken cancellationToken);
    Task<PasswordResetLink> IssueResetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Anonymous — the holder has no session, which is the situation the link exists for.</summary>
    Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken);
}
