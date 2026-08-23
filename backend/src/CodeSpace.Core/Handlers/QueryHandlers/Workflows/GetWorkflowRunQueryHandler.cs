using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>
/// The run-detail read. This is the caller that reads EVERY cell's output content — an operator expands a step and
/// inspects what it produced — so it exchanges the ledger's <c>$artifact_ref</c> pointers for the stored bytes here
/// rather than having <c>GetRunAsync</c> do it for every cell of every read. Polling projections use their bounded
/// metadata/leaf readers; this endpoint remains the explicit operator-owned body authority for normalized payload,
/// run outputs, pending action payloads and expanded cell content.
/// </summary>
public sealed class GetWorkflowRunQueryHandler : IRequestHandler<GetWorkflowRunQuery, WorkflowRunDetail?>
{
    private readonly IWorkflowService _service;
    private readonly IRunNodeOutputInflater _inflater;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunQueryHandler(IWorkflowService service, IRunNodeOutputInflater inflater, ICurrentTeam currentTeam)
    {
        _service = service;
        _inflater = inflater;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunDetail?> Handle(GetWorkflowRunQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var run = await _service.GetRunAsync(request.RunId, teamId, cancellationToken).ConfigureAwait(false);

        return run == null ? null : await _inflater.InflateAsync(run, teamId, cancellationToken).ConfigureAwait(false);
    }
}
