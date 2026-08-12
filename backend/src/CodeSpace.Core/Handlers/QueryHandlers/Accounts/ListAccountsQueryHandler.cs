using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Queries.Accounts;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Accounts;

public sealed class ListAccountsQueryHandler : IRequestHandler<ListAccountsQuery, IReadOnlyList<AccountSummary>>
{
    private readonly IAccountLifecycleService _accounts;

    public ListAccountsQueryHandler(IAccountLifecycleService accounts) { _accounts = accounts; }

    public async Task<IReadOnlyList<AccountSummary>> Handle(ListAccountsQuery request, CancellationToken cancellationToken) =>
        await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
}
