using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public interface ILegacyPlacementAdopter : IScopedDependency
{
    Task<LegacyPlacementAdoptionSummary> AdoptAsync(LegacyPlacementAdoptionRequest request, CancellationToken cancellationToken);
}

public sealed record LegacyPlacementAdoptionRequest(Guid TeamId, Guid ActorId, Guid ProfileId, int BatchSize, string? Cursor)
{
    public long ByteBudget { get; init; } = LegacyPlacementAdoptionLimits.DefaultBytesPerPass;
    public TimeSpan TimeBudget { get; init; } = LegacyPlacementAdoptionLimits.DefaultTimePerPass;
}
