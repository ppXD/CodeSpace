namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// The Library/store detail for one pack — its summary (source + freshness + counts) plus every active
/// agent + skill it contributed, ordered for display. The right pane of the store page.
/// </summary>
public sealed record PackDetail
{
    public required PackSummary Pack { get; init; }

    public IReadOnlyList<PackArtifactSummary> Artifacts { get; init; } = Array.Empty<PackArtifactSummary>();
}
