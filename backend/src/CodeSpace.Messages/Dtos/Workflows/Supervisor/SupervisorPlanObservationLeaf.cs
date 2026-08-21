using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Workflows.Supervisor;

public static class SupervisorPlanObservationLeafLimits
{
    public const int MaximumSubtasks = 20;
    public const int MaximumIdChars = 200;
    public const int MaximumTitleChars = 400;
    public const int MaximumModelChars = 200;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorPlanObservationLeafState
{
    Exact,
    Missing,
    Invalid,
    Truncated,
    Corrupt,
}

public static class SupervisorPlanObservationLeafStateExtensions
{
    public static bool IsComplete(this SupervisorPlanObservationLeafState state) => state == SupervisorPlanObservationLeafState.Exact;
}

/// <summary>
/// One bounded display leaf from a plan payload. Prefixes equal the complete strings only when their parent state is
/// Exact. TotalBytes makes a bounded prefix honest without transferring the full persisted value.
/// </summary>
public sealed record SupervisorPlanSubtaskObservationLeaf
{
    public required string IdPrefix { get; init; }
    public required int IdTotalBytes { get; init; }
    public required string TitlePrefix { get; init; }
    public required int TitleTotalBytes { get; init; }
}

/// <summary>
/// The exact-case modelUsage leaf folded into a plan outcome. Token fields preserve the existing JsonElement
/// TryGetInt32 behavior: absent, non-number and out-of-range values are null rather than invented as zero.
/// </summary>
public sealed record SupervisorPlanModelUsageObservationLeaf
{
    public required string ModelPrefix { get; init; }
    public required int ModelTotalBytes { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

/// <summary>
/// One plan decision's base observation metadata plus bounded payload/outcome leaves. Full payload, outcome,
/// instruction, rationale, phase, acceptance and other bodies are structurally absent.
/// </summary>
public sealed record SupervisorPlanObservationItem
{
    public required SupervisorDecisionObservationMetadata Metadata { get; init; }
    public required SupervisorPlanObservationLeafState SubtasksState { get; init; }
    public required int SubtasksTotalCount { get; init; }
    public required int SubtasksOmittedCount { get; init; }
    public required IReadOnlyList<SupervisorPlanSubtaskObservationLeaf> Subtasks { get; init; }
    public required SupervisorPlanObservationLeafState ModelUsageState { get; init; }
    public SupervisorPlanModelUsageObservationLeaf? ModelUsage { get; init; }
}

/// <summary>
/// Bounded Plan-only story page. SnapshotRevision is a change-feed watermark, not a cross-request MVCC snapshot;
/// Older pages may therefore show the latest leaf metadata for an older plan row.
/// </summary>
public sealed record SupervisorPlanObservationPage
{
    public required Guid SupervisorRunId { get; init; }
    public required string Mode { get; init; }
    public string? RequestCursor { get; init; }
    public required int Limit { get; init; }
    public required long SnapshotRevision { get; init; }
    public required long HeadRevision { get; init; }
    public required IReadOnlyList<SupervisorPlanObservationItem> Items { get; init; }
    public required bool HasMore { get; init; }
    public string? NextOlderCursor { get; init; }
    public required string NextNewerCursor { get; init; }
}
