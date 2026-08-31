using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

public interface IWorkflowRunModelCallBodyMaterializer : IScopedDependency
{
    Task<WorkflowRunModelCallBodyMaterializationSummary> SweepAsync(int batchSize, CancellationToken cancellationToken);
}

public interface IWorkflowRunModelCallBodyArtifactWriter
{
    Task<ArtifactMetadata?> ReadMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken);
    Task<ArtifactMetadata> PutAsync(WorkflowRunModelCallBodyArtifactWrite request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunModelCallBodyArtifactWrite(Guid TeamId, Guid CaptureId, ReadOnlyMemory<byte> Bytes, string ContentType);

public sealed class WorkflowRunModelCallBodyMaterializationSummary
{
    public int Claimed { get; internal set; }
    public int Available { get; internal set; }
    public int NotRecorded { get; internal set; }
    public int Corrupt { get; internal set; }
    public int CaptureFailed { get; internal set; }
    public int ExternalStateIndeterminate { get; internal set; }
    public int RetryScheduled { get; internal set; }
    public int LostLease { get; internal set; }
    public int Settled => Available + NotRecorded + Corrupt + CaptureFailed + ExternalStateIndeterminate;
}

internal sealed record WorkflowRunModelCallBodyMaterializerOptions
{
    public static WorkflowRunModelCallBodyMaterializerOptions Default { get; } = new();

    public int MaxConcurrency { get; init; } = 8;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxAttempts { get; init; } = 12;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(1);
    public Guid? RunFilter { get; init; }
}
