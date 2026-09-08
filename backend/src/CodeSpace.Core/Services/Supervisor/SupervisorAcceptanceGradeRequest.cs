using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>A delayed repository acceptance grade with the producer identity that authored the candidate.</summary>
public sealed record RepositoryAcceptanceGradeRequest
{
    public required Guid RepositoryId { get; init; }
    public required Guid TeamId { get; init; }
    public required string Branch { get; init; }
    public required SupervisorAcceptanceSpec Spec { get; init; }
    public required int TimeoutSeconds { get; init; }
    public OracleAnchor Anchor { get; init; } = OracleAnchor.None;
    public ReviewModelIdentity? ProducerModel { get; init; }
}

/// <summary>A delayed captured-deliverable acceptance grade with the producer identity that authored the files.</summary>
public sealed record CapturedAcceptanceGradeRequest
{
    public required Guid AgentRunId { get; init; }
    public required Guid TeamId { get; init; }
    public required SupervisorAcceptanceSpec Spec { get; init; }
    public required int TimeoutSeconds { get; init; }
    public ReviewModelIdentity? ProducerModel { get; init; }
}

/// <summary>A delayed patch acceptance grade with the producer identity that authored the patch.</summary>
public sealed record PatchAcceptanceGradeRequest
{
    public required Guid RepositoryId { get; init; }
    public required Guid TeamId { get; init; }
    public required string BaseSha { get; init; }
    public string InlinePatch { get; init; } = "";
    public Guid? PatchArtifactId { get; init; }
    public required SupervisorAcceptanceSpec Spec { get; init; }
    public required int TimeoutSeconds { get; init; }
    public IReadOnlyList<string>? OracleFloorPrograms { get; init; }
    public ReviewModelIdentity? ProducerModel { get; init; }
}
