using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Shadow-only producer bridge from a runner's durable native byte sources into Agent Run log streams. Capture
/// health is durable in those streams, while the wrapped sandbox result/exception remains authoritative and unchanged.
/// </summary>
public interface IAgentRunLogCaptureBridge : IScopedDependency
{
    Task<IAgentRunLogCaptureSession> OpenAsync(AgentRunLogCaptureOpenRequest request, CancellationToken cancellationToken);
    Task RecordGapAsync(AgentRunLogCaptureGapRequest request, CancellationToken cancellationToken);
    Task CompleteRunAsync(Guid teamId, Guid agentRunId, long workerFenceEpoch, CancellationToken cancellationToken);
}

public sealed record AgentRunLogCaptureOpenRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid ActorId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required SandboxHandle Handle { get; init; }
    public required ISandboxDurableLogSource Source { get; init; }
    public required SecretRedactor Redactor { get; init; }
}

public sealed record AgentRunLogCaptureGapRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required SandboxHandle Handle { get; init; }
    public required ISandboxDurableLogSource Source { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public interface IAgentRunLogCaptureSession
{
    SandboxHandle Handle { get; }
    Task<SandboxResult> ObserveAsync(Func<SandboxHandle, CancellationToken, Task<SandboxResult>> observer, CancellationToken cancellationToken);
}

public interface IAgentRunLogStorageResolver : IScopedDependency
{
    Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken);
}

public abstract record AgentRunLogStorageResolution
{
    private AgentRunLogStorageResolution() { }
    public sealed record Ready(Guid StorageProfileId, int StorageProfileRevision) : AgentRunLogStorageResolution;
    public sealed record Unavailable(AgentRunLogStorageProblemCode Code) : AgentRunLogStorageResolution;
}

public enum AgentRunLogStorageProblemCode
{
    Missing,
    Ambiguous,
    ResolutionFailed,
}
