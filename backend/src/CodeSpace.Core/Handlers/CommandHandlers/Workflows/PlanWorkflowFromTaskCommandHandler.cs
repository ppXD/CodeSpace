using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>
/// Thin dispatcher (Rule 16): resolves the team from <see cref="ICurrentTeam"/> (never the request body) and
/// delegates to <see cref="IWorkflowPlanningService"/>. GroundingContext stays null in Slice 1 (Slice 2 fills it).
/// </summary>
public sealed class PlanWorkflowFromTaskCommandHandler : IRequestHandler<PlanWorkflowFromTaskCommand, PlanWorkflowFromTaskResult>
{
    private readonly IWorkflowPlanningService _service;
    private readonly ICurrentTeam _currentTeam;

    public PlanWorkflowFromTaskCommandHandler(IWorkflowPlanningService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<PlanWorkflowFromTaskResult> Handle(PlanWorkflowFromTaskCommand request, CancellationToken cancellationToken) =>
        _service.PlanFromTaskAsync(new WorkflowPlanRequest { TaskText = request.TaskText, TeamId = _currentTeam.Id!.Value }, cancellationToken);
}
