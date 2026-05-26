namespace CodeSpace.Messages.Dtos.Repositories;

public sealed record BulkBindResult
{
    public required IReadOnlyList<BulkBindItemResult> Items { get; init; }
    public required int SuccessCount { get; init; }
    public required int FailureCount { get; init; }
}

public sealed record BulkBindItemResult
{
    public required string ProjectIdentifier { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? Error { get; init; }
}
