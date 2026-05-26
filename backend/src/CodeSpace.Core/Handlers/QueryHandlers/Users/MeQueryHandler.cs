using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Queries.Users;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Users;

public sealed class MeQueryHandler : IRequestHandler<MeQuery, MeResponse>
{
    private readonly IUserService _users;

    public MeQueryHandler(IUserService users) { _users = users; }

    public async Task<MeResponse> Handle(MeQuery request, CancellationToken cancellationToken) =>
        await _users.GetMeAsync(cancellationToken).ConfigureAwait(false);
}
