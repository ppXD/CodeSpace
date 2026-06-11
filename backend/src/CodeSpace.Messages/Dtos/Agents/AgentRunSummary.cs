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
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? HeartbeatAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
