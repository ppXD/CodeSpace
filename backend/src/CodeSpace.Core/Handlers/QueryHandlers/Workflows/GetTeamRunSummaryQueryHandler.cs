using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetTeamRunSummaryQueryHandler : IRequestHandler<GetTeamRunSummaryQuery, RunSummary>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetTeamRunSummaryQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<RunSummary> Handle(GetTeamRunSummaryQuery request, CancellationToken cancellationToken) =>
        _service.SummarizeTeamRunsAsync(_currentTeam.Id!.Value, request.ToFilter(), request.Today, cancellationToken);
}
