using System.Text.Json.Serialization;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>Server-issued provenance for an execution ceiling. A serialized copy supplied by a caller grants nothing.</summary>
public sealed record AgentExecutionAuthority
{
    public required int Version { get; init; }
    public required string PolicyVersion { get; init; }
    public required Guid TeamId { get; init; }
    public required Guid LogicalRunId { get; init; }
    public required string SourceKind { get; init; }
    public required string DefinitionHash { get; init; }
    public Guid? ActivationId { get; init; }
    public Guid? ActivationRevision { get; init; }
    public Guid? ParentRunId { get; init; }
    /// <summary>Server-stamped agent delegation, distinct from a workflow's parent run. Caller copies grant nothing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentAgentRunId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentAuthorityHash { get; init; }
    public required AgentAutonomyLevel GrantedCeiling { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required IReadOnlyList<AgentAuthoritySubject> Subjects { get; init; }
}

/// <summary>Immutable principal identity plus the standing to recheck; role alone is never permission for an external side effect.</summary>
public sealed record AgentAuthoritySubject
{
    public required string Kind { get; init; }
    public required Guid UserId { get; init; }
    public required Guid SecurityStamp { get; init; }
    public Guid? MembershipId { get; init; }
    public required TeamRole IssuedRole { get; init; }
    public required string Permission { get; init; }
    public required bool GlobalAdmin { get; init; }
}
