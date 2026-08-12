using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Accounts;

/// <summary>
/// Switch an account off across the whole instance. Instance-level, not team-level: it is not about
/// what someone may do in one team, it is about whether they may sign in at all.
/// </summary>
public sealed record DeactivateAccountCommand : ICommand<Unit>, IRequireGlobalAdmin
{
    public Guid UserId { get; init; }
}

public sealed record ReactivateAccountCommand : ICommand<Unit>, IRequireGlobalAdmin
{
    public Guid UserId { get; init; }
}

/// <summary>Hand someone a way back in. Answers with the link ONCE — only its digest is stored.</summary>
public sealed record IssuePasswordResetCommand : ICommand<PasswordResetLink>, IRequireGlobalAdmin
{
    public Guid UserId { get; init; }
}

/// <summary>
/// Spend a reset token. ANONYMOUS by design — someone who cannot sign in is exactly who needs this,
/// and requiring a session would make the link useless to them. The token is the credential.
/// </summary>
public sealed record ResetPasswordCommand : ICommand<Unit>, IBypassPasswordRotationGuard
{
    public string? Token { get; init; }

    public required string NewPassword { get; init; }
}
