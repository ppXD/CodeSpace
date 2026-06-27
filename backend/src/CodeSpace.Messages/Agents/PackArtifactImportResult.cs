using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The outcome of committing one selected pack artifact, keyed by its <see cref="SourcePath"/> (the stable
/// per-file identity the operator selected in the preview). The UI maps each result back onto the preview item
/// it already holds. <see cref="Kind"/> is null when the selected path matched neither an agent nor a skill at
/// the committed ref (e.g. the file was removed since the preview) — surfaced as <see cref="PackImportOutcome.Failed"/>.
/// </summary>
public sealed record PackArtifactImportResult
{
    public required string SourcePath { get; init; }
    public PackArtifactKind? Kind { get; init; }
    public required PackImportOutcome Outcome { get; init; }

    /// <summary>The persisted definition's id when the outcome is Imported/Updated; null for Skipped/Failed.</summary>
    public Guid? DefinitionId { get; init; }

    /// <summary>Human reason for a Skipped/Failed outcome; null when Imported/Updated.</summary>
    public string? Reason { get; init; }
}
