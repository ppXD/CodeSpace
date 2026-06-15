namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// One classifier's RESULT over a task (Rule 18.1, a pure data noun) — the generic <see cref="Signals"/> it
/// extracted, the effort tier + recipe it suggests, and the provenance the router records on the RoutePlan. The
/// classifier emits DATA (signals + suggestions + a confidence); the POLICY (<c>EffortPolicy</c>) and the
/// ROUTER decide what to do with it — the same model-emits-data / policy-decides separation the LLM planner
/// holds, so swapping the heuristic classifier for the (deferred) structured-LLM one changes only the
/// <see cref="Confidence"/> and the signal quality, never the routing logic.
/// </summary>
public sealed record EffortDecision
{
    /// <summary>The generic, task-type-agnostic signals the classifier extracted — the input <c>EffortPolicy.Decide</c> rules over.</summary>
    public required EffortSignals Signals { get; init; }

    /// <summary>The effort tier this classifier suggests (an open <see cref="TaskEffortModes"/> string).</summary>
    public required string SuggestedEffort { get; init; }

    /// <summary>The recipe this classifier suggests (an open <see cref="TaskRecipeKinds"/> string).</summary>
    public required string SuggestedRecipe { get; init; }

    /// <summary>The classifier's confidence in this decision, 0..1. Below <c>EffortPolicy.ConfirmConfidenceFloor</c> the router shows a confirm card. Defaults 0.</summary>
    public double Confidence { get; init; }

    /// <summary>A short human-readable why-this-tier, for the confirm card / observability. Defaults empty.</summary>
    public string Rationale { get; init; } = "";

    /// <summary>Which classifier produced this decision (an open kind string, e.g. <c>"heuristic"</c>). Defaults empty.</summary>
    public string ClassifierKind { get; init; } = "";
}
