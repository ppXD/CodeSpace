using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Dtos.Users;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Auth;

public sealed class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, ChangePasswordResponse>
{
    private readonly IUserService _users;

    public ChangePasswordCommandHandler(IUserService users) { _users = users; }

    public async Task<ChangePasswordResponse> Handle(ChangePasswordCommand request, CancellationToken cancellationToken) =>
        await _users.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, cancellationToken).ConfigureAwait(false);
}
