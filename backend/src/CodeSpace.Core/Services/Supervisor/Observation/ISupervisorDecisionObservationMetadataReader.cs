using CodeSpace.Core.Services.Supervisor.Observation.Exceptions;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;

namespace CodeSpace.Core.Services.Supervisor.Observation;

public sealed record SupervisorDecisionObservationStoryPageRequest(Guid TeamId, Guid SupervisorRunId, SupervisorDecisionObservationStoryPageMode Mode = SupervisorDecisionObservationStoryPageMode.Tail, string? Cursor = null, int Limit = SupervisorDecisionObservationPageLimits.DefaultLimit)
{
    public void ValidateShape()
    {
        var errors = new List<string>();
        if (TeamId == Guid.Empty) errors.Add("TeamId must be non-empty.");
        if (SupervisorRunId == Guid.Empty) errors.Add("SupervisorRunId must be non-empty.");
        if (!Enum.IsDefined(Mode)) errors.Add("Mode must be Tail, Older, or Newer.");
        if (Limit is < 1 or > SupervisorDecisionObservationPageLimits.MaximumLimit)
            errors.Add($"Limit must be between 1 and {SupervisorDecisionObservationPageLimits.MaximumLimit}.");
        if (Mode == SupervisorDecisionObservationStoryPageMode.Tail && Cursor != null) errors.Add("Tail does not accept a cursor.");
        if (Mode != SupervisorDecisionObservationStoryPageMode.Tail && string.IsNullOrWhiteSpace(Cursor)) errors.Add($"{Mode} requires an opaque story cursor.");
        if (errors.Count > 0) throw new SupervisorDecisionObservationReadRequestException(errors);
    }
}

public sealed record SupervisorDecisionObservationChangePageRequest(Guid TeamId, Guid SupervisorRunId, string? AfterCursor = null, int Limit = SupervisorDecisionObservationPageLimits.DefaultLimit)
{
    public void ValidateShape()
    {
        var errors = new List<string>();
        if (TeamId == Guid.Empty) errors.Add("TeamId must be non-empty.");
        if (SupervisorRunId == Guid.Empty) errors.Add("SupervisorRunId must be non-empty.");
        if (Limit is < 1 or > SupervisorDecisionObservationPageLimits.MaximumLimit)
            errors.Add($"Limit must be between 1 and {SupervisorDecisionObservationPageLimits.MaximumLimit}.");
        if (errors.Count > 0) throw new SupervisorDecisionObservationReadRequestException(errors);
    }
}

/// <summary>
/// Internal additive foundation for bounded supervisor observation. No current Room, Journal, timeline, rehydrate or
/// execution consumer uses this seam; #1615's request bundle remains the production observation path until a separate
/// parity/cutover slice. Both methods return null for foreign and absent runs, but a real owned run with no decisions
/// returns an empty page.
/// </summary>
public interface ISupervisorDecisionObservationMetadataReader
{
    Task<SupervisorDecisionObservationStoryPage?> ReadStoryPageAsync(SupervisorDecisionObservationStoryPageRequest request, CancellationToken cancellationToken);
    Task<SupervisorDecisionObservationChangePage?> ReadChangesAsync(SupervisorDecisionObservationChangePageRequest request, CancellationToken cancellationToken);
}
