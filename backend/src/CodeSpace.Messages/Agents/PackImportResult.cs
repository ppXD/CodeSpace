namespace CodeSpace.Messages.Agents;

/// <summary>
/// The result of committing a previewed pack from a URL: the resolved <see cref="PackId"/> (the idempotent sync
/// root the artifacts now belong to) and a per-selected-path <see cref="PackArtifactImportResult"/>. Re-running
/// the same commit resolves to the same pack and reports <see cref="Enums.PackImportOutcome.Updated"/> rather
/// than duplicating.
/// </summary>
public sealed record PackImportResult
{
    public required Guid PackId { get; init; }

    public IReadOnlyList<PackArtifactImportResult> Items { get; init; } = Array.Empty<PackArtifactImportResult>();
}
