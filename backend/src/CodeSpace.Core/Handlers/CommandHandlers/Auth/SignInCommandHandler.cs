using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Dtos.Users;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Auth;

public sealed class SignInCommandHandler : IRequestHandler<SignInCommand, SignInResponse>
{
    private readonly IUserService _users;

    public SignInCommandHandler(IUserService users) { _users = users; }

    public async Task<SignInResponse> Handle(SignInCommand request, CancellationToken cancellationToken) =>
        await _users.SignInAsync(request.Name, request.Password, cancellationToken).ConfigureAwait(false);
}
