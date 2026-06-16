namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The OPEN-STRING recipe kinds a <c>RoutePlan.RecipeKind</c> may name — the recipe the router chose, an open
/// string the <c>ITaskRecipeRegistry</c> resolves a recipe by. Consts (NOT an enum, Rule 18.1) so a new recipe
/// is a new const + a new recipe folder, never a core-enum edit. <see cref="SingleAgent"/> is the only kind with
/// a recipe in this PR; the rest are RESERVED names later PRs add recipes (and their projection builders) for —
/// they exist now only so the router / classifier layer can refer to them by a stable string.
/// </summary>
public static class TaskRecipeKinds
{
    /// <summary>One agent works the whole task — the only recipe with an implementation in this PR.</summary>
    public const string SingleAgent = "single-agent";

    /// <summary>RESERVED: a planner fans the task out over a <c>flow.map</c> then a synthesizer reduces. No recipe yet.</summary>
    public const string MapFanout = "map-fanout";

    /// <summary>Like <see cref="MapFanout"/>, but the planner authors a per-subtask <c>mode</c> the body maps to permissions. Opt-in only — serves no effort tier, reached by an explicit RequestedRecipe.</summary>
    public const string MapFanoutDynamic = "map-fanout-dynamic";

    /// <summary>RESERVED: a continuous durable supervisor lane. No recipe yet.</summary>
    public const string Supervisor = "supervisor";

    /// <summary>RESERVED: a coordinated multi-round loop over a checkpoint coordinator. No recipe yet.</summary>
    public const string Coordinated = "coordinated";
}
