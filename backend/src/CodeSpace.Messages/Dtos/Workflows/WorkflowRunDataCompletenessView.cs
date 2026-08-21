using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>The deliberately narrow scope of a Workflow Run data-completeness observation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunDataCompletenessScope
{
    /// <summary>Only facets with a durable producer statement are present; omitted facets remain indeterminate.</summary>
    RecordedFacetsOnly,
}

/// <summary>
/// Bounded, metadata-only observation of the completeness statements producers durably recorded for one Workflow
/// Run. This view never invents a run-wide verdict: a missing facet statement means unknown, not exact or empty.
/// </summary>
public sealed record WorkflowRunDataCompletenessView
{
    public required Guid RunId { get; init; }
    public required WorkflowRunDataCompletenessScope Scope { get; init; }
    public required IReadOnlyList<WorkflowRunDataFacetCompleteness> Facets { get; init; }
    public required bool HasStatements { get; init; }
    public required WorkflowRunCaptureCompleteness? RunWideVerdict { get; init; }
    public required bool Truncated { get; init; }
}

/// <summary>One producer's materialized statement; reading it requires no scan of that producer's record plane.</summary>
public sealed record WorkflowRunDataFacetCompleteness
{
    public required string Facet { get; init; }
    public required long? ExpectedRecordCount { get; init; }
    public required long PresentRecordCount { get; init; }
    public required long KnownMissingCount { get; init; }
    public required WorkflowRunCaptureCompleteness Verdict { get; init; }
    public required bool IsStrictlyReadable { get; init; }
    public required long Revision { get; init; }
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
}
