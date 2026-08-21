using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>Thin team-scoped dispatch to the bounded, body-blind Workflow Run display reader.</summary>
public sealed class GetWorkflowRunViewMetadataQueryHandler : IRequestHandler<GetWorkflowRunViewMetadataQuery, WorkflowRunViewMetadata?>
{
    private readonly IWorkflowRunViewMetadataReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunViewMetadataQueryHandler(IWorkflowRunViewMetadataReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunViewMetadata?> Handle(GetWorkflowRunViewMetadataQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(request.RunId, _currentTeam.Id!.Value, request.Scope, cancellationToken).ConfigureAwait(false);
}
