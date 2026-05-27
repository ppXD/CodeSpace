using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Queries.Projects;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Projects;

public sealed class ListProjectsQueryHandler : IRequestHandler<ListProjectsQuery, IReadOnlyList<ProjectSummary>>
{
    private readonly IProjectService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListProjectsQueryHandler(IProjectService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<ProjectSummary>> Handle(ListProjectsQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, cancellationToken);
}
