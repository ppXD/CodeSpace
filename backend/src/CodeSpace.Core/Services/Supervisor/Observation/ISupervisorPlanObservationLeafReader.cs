using CodeSpace.Messages.Dtos.Workflows.Supervisor;

namespace CodeSpace.Core.Services.Supervisor.Observation;

public sealed record SupervisorPlanObservationPageRequest(Guid TeamId, Guid SupervisorRunId, SupervisorDecisionObservationStoryPageMode Mode = SupervisorDecisionObservationStoryPageMode.Tail, string? Cursor = null, int Limit = SupervisorDecisionObservationPageLimits.DefaultLimit)
{
    public void ValidateShape() => new SupervisorDecisionObservationStoryPageRequest(TeamId, SupervisorRunId, Mode, Cursor, Limit).ValidateShape();
}

/// <summary>
/// Internal additive foundation for bounded plan leaves. No Journal, Room, timeline, execution, rehydrate or authority
/// consumer uses it yet because the current fact contract cannot represent omitted/truncated leaves honestly.
/// </summary>
public interface ISupervisorPlanObservationLeafReader
{
    Task<SupervisorPlanObservationPage?> ReadPageAsync(SupervisorPlanObservationPageRequest request, CancellationToken cancellationToken);
}
