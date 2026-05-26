namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Single-workflow detail. Includes the full definition for the editor and all activations
/// for the "When does this run?" sidebar.
/// </summary>
public sealed record WorkflowDetail
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool Enabled { get; init; }
    public required int LatestVersion { get; init; }
    public required WorkflowDefinition Definition { get; init; }

    /// <summary>Every configured run source on this workflow.</summary>
    public required IReadOnlyList<WorkflowActivationSummary> Activations { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
}
