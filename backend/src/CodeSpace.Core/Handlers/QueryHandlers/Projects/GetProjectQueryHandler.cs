using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Queries.Projects;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Projects;

public sealed class GetProjectQueryHandler : IRequestHandler<GetProjectQuery, ProjectSummary?>
{
    private readonly IProjectService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetProjectQueryHandler(IProjectService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<ProjectSummary?> Handle(GetProjectQuery request, CancellationToken cancellationToken) =>
        _service.GetAsync(_currentTeam.Id!.Value, request.ProjectId, cancellationToken);
}
