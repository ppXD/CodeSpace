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
