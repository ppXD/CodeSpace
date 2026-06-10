namespace CodeSpace.Messages.Enums;

/// <summary>Per-agent outcome of a pack import — the row-by-row result the UI renders after committing.</summary>
public enum AgentImportOutcome
{
    /// <summary>Persisted as a new imported persona.</summary>
    Imported,

    /// <summary>Not imported because its derived handle already exists in the team (an edited persona is never overwritten).</summary>
    Skipped,

    /// <summary>Not imported because it couldn't be parsed/fetched (a diagnostic explains why).</summary>
    Failed,
}
