using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// Operator-facing live view of one agent run — its lifecycle status, the harness driving it, and the
/// timing/heartbeat the run-detail UI uses to show "Running · last active Ns ago" and decide whether to
/// keep polling. Team-scoped at the query layer; carries no secret (the resolved key never persists, and
/// <see cref="Error"/> is already redacted at the source).
/// </summary>
public sealed record AgentRunSummary
{
    public required Guid Id { get; init; }
    public required AgentRunStatus Status { get; init; }
    public required string Harness { get; init; }

    /// <summary>The GOAL the agent was given — its instruction / prompt (for a supervisor-spawned agent, the per-subtask instruction the model authored; for an agent.run node, the node's configured goal). Read from the durable task envelope; null only when the task blob is absent/malformed. Display only.</summary>
    public string? Goal { get; init; }

    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? HeartbeatAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>The latest harness execution identity and its bounded process-attempt history. Null when capture was unavailable or no execution was recorded; it is observation only and never an Agent Run outcome authority.</summary>
    public AgentRunHarnessExecutionSummary? HarnessExecution { get; init; }

    /// <summary>Bounded capture-gap observation for this exact Agent Run. Typed unavailable rather than a false empty page when the observation plane cannot be read.</summary>
    public required AgentRunCaptureGapObservation CaptureGaps { get; init; }
}

public enum AgentRunCaptureGapReadAvailability
{
    Available,
    BackendUnavailable,
}

/// <summary>Newest-first bounded observation of gaps exactly attributed to one Agent Run.</summary>
public sealed record AgentRunCaptureGapObservation
{
    public required AgentRunCaptureGapReadAvailability Availability { get; init; }
    public required IReadOnlyList<AgentRunCaptureGapSummary> Items { get; init; }
    public required bool Truncated { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>One known-missing span and its exact frozen harness-process coordinate. Display only; never outcome authority.</summary>
public sealed record AgentRunCaptureGapSummary
{
    public required Guid Id { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid HarnessExecutionId { get; init; }
    public required Guid HarnessProcessAttemptId { get; init; }
    public required long AttemptWorkerFenceEpoch { get; init; }
    public required string SubjectKind { get; init; }
    public string? SubjectId { get; init; }
    public Guid? StreamId { get; init; }
    public string? Channel { get; init; }
    public required string RangeKind { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }
    public DateTimeOffset? RangeStartedAt { get; init; }
    public DateTimeOffset? RangeEndedAt { get; init; }
    public required string Reason { get; init; }
    public string? ReasonDetail { get; init; }
    public required string CaptureSource { get; init; }
    public required DateTimeOffset NoticedAt { get; init; }
    public required string Resolution { get; init; }
    public DateTimeOffset? RecoveredAt { get; init; }
    public string? RecoveredByKind { get; init; }
    public string? RecoveredById { get; init; }
}

/// <summary>Operator read model for the latest durable harness execution of one Agent Run.</summary>
public sealed record AgentRunHarnessExecutionSummary
{
    public required Guid Id { get; init; }
    public required int Generation { get; init; }
    public required string HarnessTypeKey { get; init; }
    public required string RunnerKind { get; init; }

    /// <summary>Open process-lifecycle vocabulary from the durable execution row; never the task verdict.</summary>
    public required string State { get; init; }

    public required int AttemptCount { get; init; }

    /// <summary>Whether at least one native frame was durably captured. Deliberately an indexed existence probe rather than an unbounded per-poll count.</summary>
    public required bool HasCapturedNativeRecords { get; init; }

    public DateTimeOffset? TerminalAt { get; init; }
    public required IReadOnlyList<AgentRunHarnessProcessAttemptSummary> Attempts { get; init; }
    public required bool AttemptsTruncated { get; init; }
}

/// <summary>One bounded physical-process observation within a harness execution. Runner locators stay private to their runner.</summary>
public sealed record AgentRunHarnessProcessAttemptSummary
{
    public required Guid Id { get; init; }
    public required int AttemptOrdinal { get; init; }

    /// <summary>Open process-lifecycle vocabulary: Running, Exited or Lost today.</summary>
    public required string State { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastObservedAt { get; init; }
    public DateTimeOffset? ExitedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? ErrorCode { get; init; }
}
