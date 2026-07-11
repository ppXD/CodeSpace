using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Queries.Projects;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Projects;

public sealed class GetProjectByRefQueryHandler : IRequestHandler<GetProjectByRefQuery, ProjectSummary?>
{
    private readonly IProjectService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetProjectByRefQueryHandler(IProjectService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<ProjectSummary?> Handle(GetProjectByRefQuery request, CancellationToken cancellationToken) =>
        _service.GetByRefAsync(_currentTeam.Id!.Value, request.IdOrSlug, cancellationToken);
}
