namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// List-row shape. Heavy fields (the definition JSON) are omitted; consumers that need it
/// hit the detail endpoint.
/// </summary>
public sealed record WorkflowSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool Enabled { get; init; }
    public required int LatestVersion { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }

    /// <summary>Decoded activation type-keys for at-a-glance "when does this run?" display.</summary>
    public required IReadOnlyList<string> ActivationTypeKeys { get; init; }
}
