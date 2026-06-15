namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The handle returned after a task is projected + started as a snapshot run (Rule 18.1, a pure data noun) —
/// the <see cref="RunId"/> the caller tracks the run by and the <see cref="ProjectionKind"/> that built it (an
/// open string, for observability). PR2-minimal: a phase-source hint the spec mentions is a later (PR5)
/// concern and is intentionally not modelled yet.
/// </summary>
public sealed record TaskRunHandle
{
    /// <summary>The <c>workflow_run.id</c> of the started snapshot run.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The projection kind that built the run — the open string the registry resolved a builder by.</summary>
    public required string ProjectionKind { get; init; }
}
