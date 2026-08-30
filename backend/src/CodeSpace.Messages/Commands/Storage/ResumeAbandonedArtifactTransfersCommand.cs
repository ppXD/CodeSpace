using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Finishes artifact transfers whose worker died mid-flight: claims the ones whose lease has expired and drives each to
/// a commit or to a terminal typed failure.
///
/// <para>No permission marker: dispatched by a recurring job on a processing pod, across every team. It never starts a
/// transfer and never uploads anything — it only completes or closes one that a caller already began.</para>
/// </summary>
public sealed record ResumeAbandonedArtifactTransfersCommand : ICommand<ResumeAbandonedArtifactTransfersResponse>;

public sealed record ResumeAbandonedArtifactTransfersResponse
{
    public required int Examined { get; init; }
    public required int Committed { get; init; }
    public required int Settled { get; init; }
    public required int Orphaned { get; init; }
    public required int Inconclusive { get; init; }
    public required int Contended { get; init; }
}
