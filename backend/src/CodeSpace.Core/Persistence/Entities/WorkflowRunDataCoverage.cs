using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The run-owned boundary around completeness applicability. A producer may append a facet while this header is open;
/// the workflow-run status trigger seals the set at a terminal transition and reopens a new generation on continue.
/// Counts may still advance for a member already in the set after sealing, so late durable accounting can improve an
/// answer without changing which questions the answer covers.
/// </summary>
public sealed class WorkflowRunDataCoverage : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string State { get; set; } = WorkflowRunDataCoverageStates.Open;
    public int Generation { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public string[] BaselineFacets { get; set; } = [];
    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
    public uint Xmin { get; set; }
}

public static class WorkflowRunDataCoverageStates
{
    public const string Open = "Open";
    public const string Sealed = "Sealed";
}

/// <summary>
/// One applicable facet in a run's persisted coverage snapshot. Membership is append-only; ordinal preserves the
/// exact baseline order captured for that run and gives conditional producers a deterministic suffix.
/// </summary>
public sealed class WorkflowRunDataCoverageFacet : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string Facet { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public int DeclaredGeneration { get; set; } = 1;
    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;
    public DateTimeOffset CreatedAt { get; set; }
}
