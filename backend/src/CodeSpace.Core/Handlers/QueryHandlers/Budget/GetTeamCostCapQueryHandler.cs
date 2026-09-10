using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Queries.Budget;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Budget;

public sealed class GetTeamCostCapQueryHandler : IRequestHandler<GetTeamCostCapQuery, TeamCostCap?>
{
    private readonly ITeamCostCapService _caps;

    public GetTeamCostCapQueryHandler(ITeamCostCapService caps) { _caps = caps; }

    public async Task<TeamCostCap?> Handle(GetTeamCostCapQuery request, CancellationToken cancellationToken) =>
        await _caps.GetAsync(cancellationToken).ConfigureAwait(false);
}
