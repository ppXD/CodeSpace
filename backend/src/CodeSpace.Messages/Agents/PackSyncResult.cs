namespace CodeSpace.Messages.Agents;

/// <summary>
/// The result of re-pulling a pack from its saved source (the store's Sync button). The already-imported
/// artifacts are refreshed in place: <see cref="UpToDate"/> were unchanged, <see cref="Updated"/> had their
/// content re-applied (handles kept). <see cref="NewArtifacts"/> are the discovered artifacts NOT yet imported
/// — surfaced as a preview for the operator to select and add (committed via the import-url path), exactly like
/// a first import. Nothing new is auto-imported by a sync.
/// </summary>
public sealed record PackSyncResult
{
    public required Guid PackId { get; init; }

    /// <summary>The git ref synced (the pack's saved branch/tag), echoed back.</summary>
    public string? Reference { get; init; }

    public required int UpToDate { get; init; }
    public required int Updated { get; init; }

    public PackPreview NewArtifacts { get; init; } = new();
}
