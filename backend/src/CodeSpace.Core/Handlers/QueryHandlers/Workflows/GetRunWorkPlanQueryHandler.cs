using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Messages.Plans;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>
/// Thin dispatcher (Rule 16) — scopes to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire) and
/// projects the run's current-plan checklist via <see cref="IWorkPlanChecklistService"/>. A foreign / absent
/// run or a plan-less run → null → the controller 404-conflates (no existence leak).
/// </summary>
public sealed class GetRunWorkPlanQueryHandler : IRequestHandler<GetRunWorkPlanQuery, WorkPlanChecklist?>
{
    private readonly IWorkPlanChecklistService _checklists;
    private readonly ICurrentTeam _currentTeam;

    public GetRunWorkPlanQueryHandler(IWorkPlanChecklistService checklists, ICurrentTeam currentTeam)
    {
        _checklists = checklists;
        _currentTeam = currentTeam;
    }

    public async Task<WorkPlanChecklist?> Handle(GetRunWorkPlanQuery request, CancellationToken cancellationToken) =>
        await _checklists.GetCurrentAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
