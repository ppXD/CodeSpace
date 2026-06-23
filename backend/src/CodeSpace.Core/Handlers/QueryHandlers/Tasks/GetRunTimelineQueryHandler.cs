using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Timeline;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Tasks;

/// <summary>
/// Thin dispatcher (Rule 16) — it scopes to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire),
/// projects the run's narrative timeline via <see cref="IRunTimelineProjector"/>, and (when the run belongs to the
/// team) reads the run's overall status team-scoped to envelope the response. A foreign / absent run → the
/// projector returns null → this returns null → the controller 404-conflates (no existence leak). All projection
/// logic lives in the projector + sources; the handler holds no DbContext + no business logic.
/// </summary>
public sealed class GetRunTimelineQueryHandler : IRequestHandler<GetRunTimelineQuery, RunTimelineResponse?>
{
    private readonly IRunTimelineProjector _projector;
    private readonly IWorkflowService _workflows;
    private readonly ICurrentTeam _currentTeam;

    public GetRunTimelineQueryHandler(IRunTimelineProjector projector, IWorkflowService workflows, ICurrentTeam currentTeam)
    {
        _projector = projector;
        _workflows = workflows;
        _currentTeam = currentTeam;
    }

    public async Task<RunTimelineResponse?> Handle(GetRunTimelineQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var events = await _projector.ProjectAsync(request.RunId, teamId, cancellationToken).ConfigureAwait(false);

        if (events == null) return null;

        var run = await _workflows.GetRunAsync(request.RunId, teamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return null;

        return new RunTimelineResponse { RunId = request.RunId, RunStatus = run.Status.ToString(), Events = events };
    }
}
