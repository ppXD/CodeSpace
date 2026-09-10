using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Commands.Budget;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Budget;

public sealed class SetTeamCostCapCommandHandler : IRequestHandler<SetTeamCostCapCommand, TeamCostCap>
{
    private readonly ITeamCostCapService _caps;

    public SetTeamCostCapCommandHandler(ITeamCostCapService caps) { _caps = caps; }

    public async Task<TeamCostCap> Handle(SetTeamCostCapCommand request, CancellationToken cancellationToken) =>
        await _caps.SetAsync(request.CapUsd, cancellationToken).ConfigureAwait(false);
}

public sealed class ClearTeamCostCapCommandHandler : IRequestHandler<ClearTeamCostCapCommand, Unit>
{
    private readonly ITeamCostCapService _caps;

    public ClearTeamCostCapCommandHandler(ITeamCostCapService caps) { _caps = caps; }

    public async Task<Unit> Handle(ClearTeamCostCapCommand request, CancellationToken cancellationToken)
    {
        await _caps.ClearAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
