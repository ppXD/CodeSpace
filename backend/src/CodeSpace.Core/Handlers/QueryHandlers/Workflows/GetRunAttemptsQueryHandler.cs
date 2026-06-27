using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>Thin dispatcher (Rule 16) — scopes to the caller's team (never the wire) and reads the lineage's attempt ladder. Foreign / absent → null → 404.</summary>
public sealed class GetRunAttemptsQueryHandler : IRequestHandler<GetRunAttemptsQuery, RunAttemptsResponse?>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetRunAttemptsQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<RunAttemptsResponse?> Handle(GetRunAttemptsQuery request, CancellationToken cancellationToken) =>
        _service.ListRunAttemptsAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken);
}
