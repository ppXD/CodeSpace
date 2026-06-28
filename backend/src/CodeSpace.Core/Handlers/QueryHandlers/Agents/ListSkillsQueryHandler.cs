using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListSkillsQueryHandler : IRequestHandler<ListSkillsQuery, IReadOnlyList<SkillDefinitionSummary>>
{
    private readonly ISkillDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListSkillsQueryHandler(ISkillDefinitionService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<SkillDefinitionSummary>> Handle(ListSkillsQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, cancellationToken);
}
