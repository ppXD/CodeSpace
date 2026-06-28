using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class GetSkillQueryHandler : IRequestHandler<GetSkillQuery, SkillDefinitionDetail?>
{
    private readonly ISkillDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetSkillQueryHandler(ISkillDefinitionService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<SkillDefinitionDetail?> Handle(GetSkillQuery request, CancellationToken cancellationToken) =>
        _service.GetAsync(_currentTeam.Id!.Value, request.SkillDefinitionId, cancellationToken);
}
