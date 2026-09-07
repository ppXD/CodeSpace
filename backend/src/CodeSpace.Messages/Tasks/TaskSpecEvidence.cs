using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks;

[JsonConverter(typeof(JsonStringEnumConverter<TaskSpecCheckSource>))]
public enum TaskSpecCheckSource
{
    [JsonStringEnumMemberName("proposed-unverified")] ProposedUnverified,
    [JsonStringEnumMemberName("user-explicit")] UserExplicit,
    [JsonStringEnumMemberName("repository-evidence")] RepositoryEvidence,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaskSpecEvidenceStatus>))]
public enum TaskSpecEvidenceStatus { Unknown, Supported, Contradicted }

[JsonConverter(typeof(JsonStringEnumConverter<TaskSpecRepositoryState>))]
public enum TaskSpecRepositoryState { Unknown, NotRequested, Unavailable, ObservedEmpty, Observed }

public sealed record TaskSpecRepositoryObservation
{
    public TaskSpecRepositoryState State { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? Reference { get; init; }
    public required string Detail { get; init; }
}

public sealed record TaskSpecAcceptanceProposal
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<string> Argv { get; init; } = Array.Empty<string>();
    public TaskSpecCheckSource Source { get; init; } = TaskSpecCheckSource.ProposedUnverified;
    public TaskSpecEvidenceStatus Status { get; init; } = TaskSpecEvidenceStatus.Unknown;
    public required string Reason { get; init; }
    public IReadOnlyList<TaskSpecEvidenceCitation> Evidence { get; init; } = Array.Empty<TaskSpecEvidenceCitation>();
    public IReadOnlyList<TaskSpecDependency> Dependencies { get; init; } = Array.Empty<TaskSpecDependency>();
    public required string CommandDigest { get; init; }
    public required string SourceDigest { get; init; }
}

/// <summary>Server-confirmed source identity/content digest and the reviewer's original excerpt. The excerpt's existence only establishes citation integrity.</summary>
public sealed record TaskSpecEvidenceCitation
{
    public required string SourceId { get; init; }
    public required string Kind { get; init; }
    public string? Path { get; init; }
    public string? Reference { get; init; }
    public required string ContentDigest { get; init; }
    public required string Quote { get; init; }
}

/// <summary>A proposed prerequisite and a strategy to test it. Neither field asserts that the strategy has run.</summary>
public sealed record TaskSpecDependency
{
    public required string Requirement { get; init; }
    public required string ValidationStrategy { get; init; }
}

public sealed record TaskSpecModelCall
{
    public required string Phase { get; init; }
    public required string Outcome { get; init; }
    public string? SelectedModel { get; init; }
    public string? ActualModel { get; init; }
    public IReadOnlyList<string> FailedOver { get; init; } = Array.Empty<string>();
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public bool UsageMayBeIncomplete { get; init; } = true;
    public long ElapsedMilliseconds { get; init; }
}
