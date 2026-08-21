using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>The bounded reason an active-plan resolver's recorded compact result cannot authorize a reviewable head.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorResolveContributorIssueKind
{
    MalformedRecordedOutcome,
    MissingRow,
    CrossTeam,
    NonTerminalRow,
    MissingRequiredResult,
    MalformedResult,
    ResultStatusMismatch,
    CompactResultMismatch,
}

/// <summary>An integrity-failed resolution requires review and can never become publish authority.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorResolveContributorIntegrityStatus
{
    NeedsReview,
}

/// <summary>
/// One fixed-size fact for the resolver a <c>resolve</c> decision must have staged. It deliberately carries no result
/// JSON, summary, branch, repository result, error, or exception text, so corrupt/untrusted bytes are never copied
/// into a second durable shape.
/// </summary>
public sealed record SupervisorResolveContributorIntegrity
{
    public SupervisorResolveContributorIntegrityStatus Status { get; init; } = SupervisorResolveContributorIntegrityStatus.NeedsReview;
    public Guid? AgentRunId { get; init; }
    public required SupervisorResolveContributorIssueKind Kind { get; init; }
}
