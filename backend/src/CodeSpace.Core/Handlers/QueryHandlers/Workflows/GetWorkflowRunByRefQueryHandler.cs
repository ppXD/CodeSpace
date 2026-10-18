using CodeSpace.Core.Services.Credentials.Remedy;
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
    private readonly IProviderRemedyService _remedies;

    public GetWorkflowRunByRefQueryHandler(IWorkflowService service, IRunNodeOutputInflater inflater, ICurrentTeam currentTeam, IProviderRemedyService remedies)
    {
        _service = service;
        _inflater = inflater;
        _currentTeam = currentTeam;
        _remedies = remedies;
    }

    public async Task<WorkflowRunDetail?> Handle(GetWorkflowRunByRefQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var run = await _service.GetRunByRefAsync(request.IdOrNumber, teamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return null;

        var detail = await _inflater.InflateAsync(run, teamId, cancellationToken).ConfigureAwait(false);

        // What is wrong with a provider connection the run failed on, and what THIS caller can do about it — only a finished run has one.
        return WorkflowRunState.IsTerminal(detail.Status) ? detail with { Remedy = await _remedies.GetForRunAsync(detail.Id, teamId, cancellationToken).ConfigureAwait(false) } : detail;
    }
}
