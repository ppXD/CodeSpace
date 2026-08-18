using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional sibling capability for a durable runner whose native stdout/stderr sources can be replayed as bounded
/// byte ranges. The contract is harness-neutral and byte-addressed: callers never decode at a read boundary, and a
/// future container/job runner can implement the same seam without exposing a filesystem path.
/// </summary>
public interface ISandboxDurableLogSource
{
    IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle);
    Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken);
}

public sealed record SandboxDurableLogDescriptor(string SourceKey, string StreamKind, string ContentType, string? ContentEncoding, string CaptureSource);

public sealed record SandboxDurableLogReadRequest
{
    public required SandboxHandle Handle { get; init; }
    public required string SourceKey { get; init; }
    public required long OffsetBytes { get; init; }
    public required int MinimumBytes { get; init; }
    public required int MaximumBytes { get; init; }
    public bool FinalDrain { get; init; }
}

public abstract record SandboxDurableLogReadResult
{
    private SandboxDurableLogReadResult() { }
    public sealed record Available(ReadOnlyMemory<byte> Bytes) : SandboxDurableLogReadResult;
    /// <summary>The producer is gone and the durable byte source remained quiescent across the runner's seal check. This is the only result that authorizes a final capture receipt. <paramref name="Truncated"/> is true when the source hit its own size cap and therefore proves only the CAPPED HEAD of what the producer wrote — the receipt is complete, the content is not.</summary>
    public sealed record EndOfSource(bool Truncated = false) : SandboxDurableLogReadResult;
    /// <summary>No bytes are currently readable. This is always transient and must never be interpreted as EOF.</summary>
    public sealed record NoData : SandboxDurableLogReadResult;
    public sealed record Unavailable(SandboxDurableLogProblem Problem) : SandboxDurableLogReadResult;
}

public sealed record SandboxDurableLogProblem(SandboxDurableLogProblemCode Code, bool IsRetryable = false);

public enum SandboxDurableLogProblemCode
{
    InvalidRequest,
    UnknownSource,
    SourceMissing,
    SourceReset,
    AccessDenied,
    IoUnavailable,
}
