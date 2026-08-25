using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;

/// <summary>
/// The shared skeleton of the plan→map→agent→synth→done projection FAMILY (Rule 18 — the structure both
/// plan-map variants share, specialized only where they actually differ). The planner is the TRIAD's
/// <c>plan.author</c> node (S4b): it authors a structured, DURABLE plan — a versioned WorkPlan row the run's
/// checklist renders — and its <c>json.subtasks</c> output binds the map exactly like the structured
/// <c>llm.complete</c> it replaced (the node's <c>json</c> output is binding-compatible by contract). The
/// operator's planner critic (<c>reviewMode</c> None|Gate|Improve → the CriticPlannerDecorator) and pinned
/// planner model ride the node config. The plan is FLAT (<c>flatPlan</c>): the map fans every subtask out in
/// parallel, so the planner is constrained to independent items (authored dependsOn is stripped, logged).
///
/// <para>The graph shape + edges, the parallelism cap, the synth reduce, and the done terminal are IDENTICAL
/// across variants; the ONLY divergence left is the body agent's goal binding + optional per-branch mode —
/// the two hooks below. A fix to the shared spine lands once, here, for every variant. <see cref="Build"/>
/// stays PURE — the planner is a NODE that runs at execution, not a build-time LLM call — so the output always
/// passes the real <c>DefinitionValidator</c>. The base is abstract + not <c>ISingletonDependency</c>, so only
/// the concrete variants self-register.</para>
/// </summary>
public abstract class PlanMapBuilderBase : IWorkflowDefinitionBuilder
{
    /// <summary>The projection kind this variant registers under (the key <c>ITaskProjectionRegistry</c> resolves by).</summary>
    public abstract string ProjectionKind { get; }

    /// <summary>The body agent's goal binding over the planner's subtask objects — e.g. <c>"{{item.instruction}}"</c>.</summary>
    protected abstract string BranchGoal { get; }

    /// <summary>The body agent's per-branch mode binding (e.g. <c>"{{item.kind}}"</c> — the plan item's open kind), or null when the variant authors no mode — then <see cref="AgentNodeMapping.BuildAgentConfig"/> omits it (byte-identical to a no-mode node).</summary>
    protected virtual string? BranchMode => null;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        CompletionMode = context.CompletionMode,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(context),
    };

    private IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

            new() { Id = "planner", TypeKey = "plan.author", Label = "Plan",
                    Config = PlannerConfig(context), Inputs = PlannerInputs(context) },
        };

        // The confirm gate (S4d): the operator opted into confirm-plan-first, so the run PARKS on the authored
        // plan and the map binds the CONFIRM node's outputs — always the APPROVED version, never a rejected one.
        // The gate node carries the SAME planner config so a revision re-plans under the same model + critic.
        if (context.RequirePlanConfirmation)
            nodes.Add(new() { Id = "confirm", TypeKey = "plan.confirm", Label = "Confirm plan",
                              Config = PlannerConfig(context), Inputs = PlannerInputs(context) });

        nodes.AddRange(new NodeDefinition[]
        {
            new() { Id = "map", TypeKey = "flow.map", Label = "Fan out", Config = MapConfigJson(context), Inputs = MapInputs(context) },

            new() { Id = "ms", TypeKey = "flow.map_start", Label = "Subtask", ParentId = "map", Config = Empty(), Inputs = Empty() },

            new() { Id = "agent", TypeKey = "agent.run", Label = "Work the subtask", ParentId = "map", Retry = AgentNodeMapping.DefaultRetry,
                    Config = AgentNodeMapping.BuildAgentConfig(BranchGoal, context.AgentProfile, BranchMode, grounding: context.GroundingContext, acceptance: "{{item.acceptance}}"), Inputs = AgentNodeMapping.BuildAgentInputs(context) },
        });

        // P4 (the plan-map integrated candidate): a repo-bound fan-out integrates its produced work into ONE
        // reviewable branch before the narration reduce — "integrate, not narrate" for the code half. Run-sourced
        // (the node derives contributions from the run's own publish ledger), so nothing new threads through map
        // outputs. A conflict is a routable outcome the synth then narrates over; a repo-less task emits no node
        // and stays byte-identical.
        // parkOnConflict: a conflicted candidate PARKS for review (the wait carries the conflict detail; the
        // resumed pass re-integrates) — fragments never narrate past a human silently ("conflict ⇒ park").
        if (context.AgentProfile?.RepositoryId is { } repositoryId)
            nodes.Add(new() { Id = "integrate", TypeKey = "git.integrate_run", Label = "Integrate",
                              Config = JsonSerializer.SerializeToElement(new { parkOnConflict = true }),
                              Inputs = JsonSerializer.SerializeToElement(new { repositoryId = repositoryId.ToString() }) });

        nodes.AddRange(new NodeDefinition[]
        {
            new() { Id = "synth", TypeKey = "llm.complete", Label = "Synthesize",
                    Config = SynthConfig(context), Inputs = SynthInputs(context) },

            new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(), Inputs = DoneInputs(context) },
        });

        return nodes;
    }

    private static IReadOnlyList<EdgeDefinition> BuildEdges(TaskBuildContext context)
    {
        var edges = new List<EdgeDefinition> { new() { From = "start", To = "planner" } };

        if (context.RequirePlanConfirmation)
            edges.AddRange(new EdgeDefinition[] { new() { From = "planner", To = "confirm" }, new() { From = "confirm", To = "map" } });
        else
            edges.Add(new() { From = "planner", To = "map" });

        // The integrate step sequences between the fan-out and the narration reduce (map → integrate → synth);
        // a repo-less graph keeps the original map → synth edge byte-identically.
        if (context.AgentProfile?.RepositoryId is not null)
            edges.AddRange(new EdgeDefinition[] { new() { From = "map", To = "integrate" }, new() { From = "integrate", To = "synth" } });
        else
            edges.Add(new() { From = "map", To = "synth" });

        edges.AddRange(new EdgeDefinition[] { new() { From = "synth", To = "done" }, new() { From = "ms", To = "agent" } });

        return edges;
    }

    /// <summary>The plan.author Config — always a FLAT plan (the parallel map cannot honor ordering), plus the launch's pinned planner model row + the operator's planner critic (reviewMode / reviewerModelId, omitted when off — byte-identical).</summary>
    private static JsonElement PlannerConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>
        {
            ["flatPlan"] = true,
        };

        AddIfPresent(config, "plannerModelId", context.PlannerModelRowId?.ToString());
        AddIfPresent(config, "reviewMode", context.PlannerReviewMode != ReviewMode.None ? (int)context.PlannerReviewMode : null);
        AddIfPresent(config, "reviewerModelId", context.PlannerReviewMode != ReviewMode.None ? context.ReviewerModelId?.ToString() : null);
        // D① grounded plan review — a real read-only agent verifies the plan against the bound repository's tree.
        // Rides only when a plan review is active AND the profile binds a repo (else omitted — byte-identical).
        var reviewerAgentOn = context.PlannerReviewMode != ReviewMode.None && context.AgentProfile?.ReviewerAgent == true && context.AgentProfile?.RepositoryId is not null;
        AddIfPresent(config, "reviewerAgent", reviewerAgentOn ? true : (bool?)null);
        AddIfPresent(config, "repositoryId", reviewerAgentOn ? context.AgentProfile!.RepositoryId!.Value.ToString() : null);
        // S1: the reviewer clones at the launch's immutable base pin — the SAME commit the fan-out agents materialize,
        // so the tree the plan is verified against can never drift from the tree the plan executes on.
        AddIfPresent(config, "pinnedSha", reviewerAgentOn && context.PinnedShas is { } pins && pins.TryGetValue(context.AgentProfile!.RepositoryId!.Value, out var pin) ? pin : null);

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The plan.author Inputs — the seed goal (+ the launch grounding when present, so a continue's prior-turn digest steers the plan; + the operator's acceptance criteria, so the plan and its per-item contracts target the definition of done — S5b).</summary>
    private static JsonElement PlannerInputs(TaskBuildContext context)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["goal"] = context.Seed.Goal,
        };

        AddIfPresent(inputs, "grounding", NullIfBlank(context.GroundingContext));
        AddIfPresent(inputs, "criteria", context.AcceptanceCriteria is { Count: > 0 } criteria ? criteria.ToList() : null);

        return JsonSerializer.SerializeToElement(inputs);
    }

    /// <summary>The map Inputs — fan out over the plan's typed subtasks array. Under the confirm gate the map binds the CONFIRM node (always the APPROVED version); ungated it binds the planner directly (byte-identical to pre-gate).</summary>
    private static JsonElement MapInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        items = context.RequirePlanConfirmation ? "{{nodes.confirm.outputs.json.subtasks}}" : "{{nodes.planner.outputs.json.subtasks}}",
    });

    /// <summary>The map Config — the route's <see cref="RouteCaps.MaxParallelism"/> cap so the fan-out is bounded (the engine reads the <c>maxParallelism</c> key into the branch SemaphoreSlim via <c>MapConfig</c>), the route's spend ceiling, both written only when the route sets them, and always the reduce's input budget (<see cref="SynthPromptBudgetChars"/>) that <see cref="SynthInputs"/>'s bounded binding depends on.</summary>
    private static JsonElement MapConfigJson(TaskBuildContext context)
    {
        var config = new JsonObject();

        if (context.Route.Caps.MaxParallelism is { } parallelism) config["maxParallelism"] = parallelism;

        // The route computes a spend ceiling for every lane, and the supervisor builder has always passed its own
        // through (SupervisorDefinitionBuilder writes "maxCostUsd" the same way). This one dropped it, so a Standard
        // run's operator could set a budget the engine then ignored entirely — the deep lane refused work over its
        // cap while the map lane fanned out until the model plane or the wall clock stopped it.
        if (context.Route.Caps.MaxCostUsd is { } costCap) config["maxCostUsd"] = costCap;

        // The reduce's INPUT bound. The synth prompt below binds the map's bounded projection rather than the raw
        // array, so this key is what makes that projection exist. Resolved HERE, at build time, and recorded in the
        // definition: a replayed run then projects against the number its own snapshot froze, never a later host's.
        config["promptBudgetChars"] = SynthPromptBudgetChars;

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The synth Config — an omitted provider inherits the team's ranked pool; the profile's optional model remains a pin. This keeps Standard projection generic across Anthropic, OpenAI, and Custom pools.</summary>
    private static JsonElement SynthConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>();

        AddIfPresent(config, "model", NullIfBlank(context.AgentProfile?.Model));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>
    /// Env var (a positive integer) overriding the reduce's input character budget, for an operator whose pool
    /// serves a narrower-context model than the default assumes. Rule 8; the literal is pinned by test.
    /// </summary>
    public const string SynthPromptBudgetCharsEnvVar = "CODESPACE_PLAN_MAP_SYNTH_PROMPT_BUDGET_CHARS";

    /// <summary>
    /// The reduce's input character budget. 120K characters is roughly 40K tokens on this repo's own chars/3 estimate
    /// (<c>LlmBudgetGuard.EstimateUsd</c>): well inside the 200K-token window of the Anthropic models this synth
    /// defaults to, and far above any ordinary fan-out, so the common case never reaches the bound and its prompt
    /// stays byte-identical. It does NOT fit every narrow-context model — an operator whose pool serves one lowers it
    /// through <see cref="SynthPromptBudgetCharsEnvVar"/>; that is what the override is for.
    /// </summary>
    public const int SynthPromptBudgetCharsDefault = 120_000;

    /// <summary>The resolved budget: the env override when a positive integer, else <see cref="SynthPromptBudgetCharsDefault"/>. Read at BUILD time so the number lands in the definition snapshot and a replay cannot drift.</summary>
    public static int SynthPromptBudgetChars => LlmModelCapabilities.ResolvePositive(Environment.GetEnvironmentVariable(SynthPromptBudgetCharsEnvVar), SynthPromptBudgetCharsDefault);

    /// <summary>
    /// The synth Inputs — a REAL reduce prompt: combine ALL per-branch results into one coherent answer that
    /// addresses the seed goal. The userPrompt embeds the goal AND the map's results, bound through the map's
    /// BUDGET-BOUNDED projection (<c>{{nodes.map.outputs.resultsPrompt}}</c>, see
    /// <see cref="MapConfigJson"/>'s <c>promptBudgetChars</c>) rather than the raw array.
    ///
    /// <para>Binding the raw array made the reduce generic over any subtask count in the sense that it never
    /// mentioned one — but nothing capped the prompt, so a wide fan-out, or a few branches with large outputs,
    /// built a request past the model's context window. That 400 is not parkable, so the reduce died at the LAST
    /// node, after every branch had already run, billed, and (on a repo-bound graph) integrated. Bound to the
    /// projection, the reduce is generic over any subtask count AND any branch size: within budget the projection
    /// is the identical serialization of the identical array, so an ordinary run's prompt does not change by a
    /// character; over budget the model is handed a fair share of every included branch and TOLD, in the prompt's
    /// first sentence, that it is reading an excerpt.</para>
    /// </summary>
    private static JsonElement SynthInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        systemPrompt = "Combine the per-subtask results into one coherent answer that addresses the goal.",
        userPrompt = $"Goal: {context.Seed.Goal}\n\nPer-subtask results:\n" + SynthResultsRef,
    });

    /// <summary>The reduce's results binding, composed from <see cref="WorkflowOutputKeys.MapResultsPrompt"/> so the prompt and the key the reducer writes cannot drift apart.</summary>
    private const string SynthResultsRef = "{{nodes.map.outputs." + WorkflowOutputKeys.MapResultsPrompt + "}}";

    /// <summary>The coverage of the results the reduce actually read, composed from <see cref="WorkflowOutputKeys.MapResultsCoverage"/> the same way. A sole-placeholder binding resolves to the WHOLE object, so the run output carries the fact intact rather than a stringified copy.</summary>
    private const string SynthResultsCoverageRef = "{{nodes.map.outputs." + WorkflowOutputKeys.MapResultsCoverage + "}}";

    /// <summary>
    /// The done terminal Inputs — bind the synth's reduced <c>text</c> output into the run's <c>combined</c> output
    /// (the llm.complete node's output key is <c>text</c>). A repo-bound graph also surfaces the integrated candidate
    /// (branch + whole-set status) as run outputs, so the reviewable head is readable off the run row, not just the
    /// node ledger.
    ///
    /// <para><c>resultsCoverage</c> rides the same reasoning one step further: <c>combined</c> is a single prose
    /// answer that reads as though it addressed every subtask, and when the reduce's input was excerpted it did not.
    /// The coverage the map recorded travels onto the RUN ROW beside the answer it qualifies, so anyone reading the
    /// run's outcome — not only someone who opens the map node's bag — sees the number of subtasks the answer is
    /// actually based on. It is bound on every plan-map graph because the map declares the budget on every one.</para>
    /// </summary>
    private static JsonElement DoneInputs(TaskBuildContext context)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["combined"] = "{{nodes.synth.outputs.text}}",
            [WorkflowOutputKeys.MapResultsCoverage] = SynthResultsCoverageRef,
        };

        if (context.AgentProfile?.RepositoryId is not null)
        {
            inputs["integrationStatus"] = "{{nodes.integrate.outputs.status}}";
            inputs["integratedBranch"] = "{{nodes.integrate.outputs.integratedBranch}}";
        }

        return JsonSerializer.SerializeToElement(inputs);
    }

    protected static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    protected static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }

    protected static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
