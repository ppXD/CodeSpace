using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>Per-agent outcome of an import commit, keyed by source path — the UI renders one row each (imported / skipped / failed + why).</summary>
public sealed record AgentImportResult
{
    public required string SourcePath { get; init; }
    public required AgentImportOutcome Outcome { get; init; }

    /// <summary>Human reason for a Skipped/Failed outcome; null when Imported.</summary>
    public string? Reason { get; init; }

    /// <summary>The created persona's id when <see cref="Outcome"/> is Imported; null otherwise.</summary>
    public Guid? AgentDefinitionId { get; init; }
}
