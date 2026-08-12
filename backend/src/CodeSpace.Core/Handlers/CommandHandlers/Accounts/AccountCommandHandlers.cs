using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Commands.Accounts;
using CodeSpace.Messages.Dtos.Users;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Accounts;

public sealed class DeactivateAccountCommandHandler : IRequestHandler<DeactivateAccountCommand, Unit>
{
    private readonly IAccountLifecycleService _accounts;

    public DeactivateAccountCommandHandler(IAccountLifecycleService accounts) { _accounts = accounts; }

    public async Task<Unit> Handle(DeactivateAccountCommand request, CancellationToken cancellationToken)
    {
        await _accounts.DeactivateAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class ReactivateAccountCommandHandler : IRequestHandler<ReactivateAccountCommand, Unit>
{
    private readonly IAccountLifecycleService _accounts;

    public ReactivateAccountCommandHandler(IAccountLifecycleService accounts) { _accounts = accounts; }

    public async Task<Unit> Handle(ReactivateAccountCommand request, CancellationToken cancellationToken)
    {
        await _accounts.ReactivateAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class IssuePasswordResetCommandHandler : IRequestHandler<IssuePasswordResetCommand, PasswordResetLink>
{
    private readonly IAccountLifecycleService _accounts;

    public IssuePasswordResetCommandHandler(IAccountLifecycleService accounts) { _accounts = accounts; }

    public async Task<PasswordResetLink> Handle(IssuePasswordResetCommand request, CancellationToken cancellationToken) =>
        await _accounts.IssueResetAsync(request.UserId, cancellationToken).ConfigureAwait(false);
}

public sealed class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, Unit>
{
    private readonly IAccountLifecycleService _accounts;

    public ResetPasswordCommandHandler(IAccountLifecycleService accounts) { _accounts = accounts; }

    public async Task<Unit> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        await _accounts.ResetPasswordAsync(request.Token ?? string.Empty, request.NewPassword, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
