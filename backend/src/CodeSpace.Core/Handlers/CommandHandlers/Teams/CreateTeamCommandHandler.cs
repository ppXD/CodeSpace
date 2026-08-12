using CodeSpace.Core.Services.Teams;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Dtos.Teams;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Teams;

public sealed class CreateTeamCommandHandler : IRequestHandler<CreateTeamCommand, TeamSummary>
{
    private readonly ITeamProvisioningService _teams;

    public CreateTeamCommandHandler(ITeamProvisioningService teams) { _teams = teams; }

    public async Task<TeamSummary> Handle(CreateTeamCommand request, CancellationToken cancellationToken) =>
        await _teams.CreateAsync(request.Name, cancellationToken).ConfigureAwait(false);
}
