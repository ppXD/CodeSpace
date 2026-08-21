using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>Thin team-scoped dispatch to the observation-only completeness reader.</summary>
public sealed class GetWorkflowRunDataCompletenessQueryHandler : IRequestHandler<GetWorkflowRunDataCompletenessQuery, WorkflowRunDataCompletenessView?>
{
    private readonly IRunDataCompletenessReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunDataCompletenessQueryHandler(IRunDataCompletenessReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunDataCompletenessView?> Handle(GetWorkflowRunDataCompletenessQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
