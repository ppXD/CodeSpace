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

    /// <summary>
    /// The HOST's own tear-down signal (<c>IHostApplicationLifetime.ApplicationStopping</c>). Raised ⇒ the FINAL drain
    /// stops waiting out a destination that is refusing writes and parks instead: the stream stays Open at its own
    /// fence with its stall marker, for the capture recovery sweep to re-claim and finish.
    ///
    /// <para>What the waiting costs depends on which capture is draining, and it is never nothing. A session opened
    /// for the live run holds the job's own token, so a drain that will not end is a worker tear-down that never
    /// STARTS — it runs from the cancellation that drain is refusing to observe. A session the tear-down itself opens
    /// to re-fold the dead agent holds the worker's lease-landing budget, so every second the drain spends there is a
    /// second the run's terminal write does not get.</para>
    ///
    /// <para>Default (None) means no host tear-down is in progress, which is every ordinary run: the live and final
    /// retry cadences are exactly what they were.</para>
    /// </summary>
    public CancellationToken HostShutdown { get; init; }
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

/// <summary>
/// Establishes the explicit Settings-visible default route for a team that has never configured Agent Run log storage,
/// but only when the deployment explicitly qualified local-rwx as shared. Implementations may act only on a missing
/// exact route; lifecycle states and reserved stable names chosen by an operator are final.
/// </summary>
public interface IAgentRunLogStorageReadiness : IScopedDependency
{
    Task EnsureDefaultRouteAsync(Guid teamId, CancellationToken cancellationToken);
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
    Inactive,
    Invalid,
}
