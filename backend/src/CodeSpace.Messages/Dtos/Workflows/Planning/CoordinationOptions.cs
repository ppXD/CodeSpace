namespace CodeSpace.Messages.Dtos.Workflows.Planning;

/// <summary>
/// The operator's GOALS (not graph) for an L3 checkpoint-coordinated projection: the knobs that shape the
/// generated <c>flow.loop</c> + <c>flow.map</c> skeleton — how many rounds the coordinator may re-decide
/// across, and how wide each round's fan-out runs at once. A data noun (Rule 18.1) the planning service
/// threads from the command into <c>WorkflowPlanProjector.ProjectCoordinated</c>. The operator never names a
/// node or an edge; the projector owns the skeleton and folds these bounds into the loop config.
///
/// <para>Both bounds are clamped by the engine's own caps at run time (loop <c>maxIterations</c>, map
/// <c>maxParallelism</c>); these are the projection-time defaults a save-time validator also sees.</para>
/// </summary>
public sealed record CoordinationOptions
{
    /// <summary>Hard cap on coordination rounds — the loop's <c>maxIterations</c>. The coordinator re-decides at the end of each round; the loop stops earlier on a <c>done</c>/<c>abort</c> decision. Default 5.</summary>
    public int MaxRounds { get; init; } = DefaultMaxRounds;

    /// <summary>Optional cap on how many subtask branches run at once within a round (the map's <c>maxParallelism</c>). Null inherits the engine-wide default.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>The default round cap when the operator doesn't set one — a sane bound that lets the coordinator iterate a few times without an unbounded loop.</summary>
    public const int DefaultMaxRounds = 5;
}
