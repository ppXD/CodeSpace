using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>A bounded reason why one active-generation agent-run id could not become a trustworthy merge contributor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorMergeContributorIssueKind
{
    MissingRow,
    CrossTeam,
    NonTerminalRow,
    MissingRequiredResult,
    MalformedResult,
    ResultStatusMismatch,
}

/// <summary>The merge cannot authorize a complete head until every recorded contributor has a trustworthy fact.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorMergeContributorIntegrityStatus
{
    NeedsReview,
}

/// <summary>One recorded contributor identity plus its integrity failure. Never carries result JSON, patches, errors, or tenant data.</summary>
public sealed record SupervisorMergeContributorIssue
{
    public required Guid AgentRunId { get; init; }
    public required SupervisorMergeContributorIssueKind Kind { get; init; }
}

/// <summary>
/// The bounded, persisted merge fact emitted only when one or more active-generation contributor ids could not be
/// materialized faithfully. It makes a partial read explicit without copying unbounded agent results into a second
/// shape. Healthy merges omit this object and retain their historical bytes.
/// </summary>
public sealed record SupervisorMergeContributorIntegrity
{
    public SupervisorMergeContributorIntegrityStatus Status { get; init; } = SupervisorMergeContributorIntegrityStatus.NeedsReview;
    public required int ExpectedCount { get; init; }
    public required int MaterializedCount { get; init; }
    public IReadOnlyList<SupervisorMergeContributorIssue> Issues { get; init; } = Array.Empty<SupervisorMergeContributorIssue>();
}
