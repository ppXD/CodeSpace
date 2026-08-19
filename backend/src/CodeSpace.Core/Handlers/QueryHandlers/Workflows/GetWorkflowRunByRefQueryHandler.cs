using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>
/// The clean-URL run-detail read (<c>/runs/{number}</c>). Same wire shape as <see cref="GetWorkflowRunQueryHandler"/>,
/// including the on-demand exchange of offloaded output refs for their stored bytes.
/// </summary>
public sealed class GetWorkflowRunByRefQueryHandler : IRequestHandler<GetWorkflowRunByRefQuery, WorkflowRunDetail?>
{
    private readonly IWorkflowService _service;
    private readonly IRunNodeOutputInflater _inflater;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunByRefQueryHandler(IWorkflowService service, IRunNodeOutputInflater inflater, ICurrentTeam currentTeam)
    {
        _service = service;
        _inflater = inflater;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunDetail?> Handle(GetWorkflowRunByRefQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var run = await _service.GetRunByRefAsync(request.IdOrNumber, teamId, cancellationToken).ConfigureAwait(false);

        return run == null ? null : await _inflater.InflateAsync(run, teamId, cancellationToken).ConfigureAwait(false);
    }
}
