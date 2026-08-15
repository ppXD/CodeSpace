using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>Durable expected-stream declaration and bounded recovery for AgentRun log health only.</summary>
public interface IAgentRunLogCaptureRecoveryService : IScopedDependency
{
    Task<AgentRunLogCaptureDeclarationResult> DeclareAsync(AgentRunLogCaptureDeclarationRequest request, CancellationToken cancellationToken);
    Task<AgentRunLogCaptureRecoverySummary> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed record AgentRunLogCaptureDeclarationRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }
    public required IReadOnlyList<AgentRunLogExpectedStream> Streams { get; init; }
}

public sealed record AgentRunLogExpectedStream(string StreamKind, string ContentType, string? ContentEncoding, string CaptureSource);

public abstract record AgentRunLogCaptureDeclarationResult
{
    private AgentRunLogCaptureDeclarationResult() { }
    public sealed record Declared(int Created, int Existing) : AgentRunLogCaptureDeclarationResult;
    public sealed record Rejected(AgentRunLogCaptureDeclarationProblem Code) : AgentRunLogCaptureDeclarationResult;
}

public enum AgentRunLogCaptureDeclarationProblem
{
    InvalidRequest,
    MissingRun,
    RunNotRunning,
    StaleWorker,
    IdentityConflict,
}

public sealed record AgentRunLogCaptureRecoverySummary(int Claimed, int Completed, int CaptureFailed, int Superseded, int Retried, int LostLease)
{
    public int ExternalStateIndeterminate { get; init; }
}
