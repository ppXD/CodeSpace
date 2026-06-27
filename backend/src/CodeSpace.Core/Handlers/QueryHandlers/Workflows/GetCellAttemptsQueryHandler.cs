using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>Thin dispatcher (Rule 16) — scopes to the caller's team (never the wire) and reads one cell's attempt history. Foreign / absent → null → 404.</summary>
public sealed class GetCellAttemptsQueryHandler : IRequestHandler<GetCellAttemptsQuery, CellAttemptsResponse?>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetCellAttemptsQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<CellAttemptsResponse?> Handle(GetCellAttemptsQuery request, CancellationToken cancellationToken) =>
        _service.ListCellAttemptsAsync(request.RunId, request.NodeId, request.IterationKey, _currentTeam.Id!.Value, cancellationToken);
}
