using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// The L3 CHECKPOINT-COORDINATED projection (Rule 18.3 — a coordinated variant beside the one-shot
/// <see cref="WorkflowPlanProjector.Project"/>). Composes EXISTING nodes only — no new engine code, node kind,
/// wait kind, or migration. The model re-decides BETWEEN rounds: plan → parallel work → a coordinator judges
/// the round's results → rework / done / abort, bounded by a round cap. The emitted graph:
///
/// <code>
/// trigger.manual ─▶ flow.wait_approval ─▶ logic.if(approved)
///                                            ├─true ▶ flow.loop[ subtasks, decision; term: decision∈{done,abort}; maxIterations = MaxRounds ]
///                                            │           loop_start ▶ flow.map(items = {{loop.subtasks}})
///                                            │                            map_start ▶ [coding ? agent.run : llm.complete]
///                                            │         ▶ coordinator (llm.complete + CoordinatorSchema)
///                                            │        ▶ synthesizer (llm.complete) ▶ Done
///                                            └─false ▶ Rejected (End)
/// </code>
///
/// <para><b>The loop wiring is the spine.</b> Two loop variables thread the round state:
/// <c>subtasks</c> seeds round 1 from <c>{{input.subtasks}}</c> (the baked plan) and re-seeds each later round
/// from <c>{{nodes.coordinator.outputs.json.reworkSubtasks}}</c>; <c>decision</c> starts <c>"rework"</c> and
/// updates from <c>{{nodes.coordinator.outputs.json.decision}}</c>. The loop terminates (logic <c>or</c>) when
/// <c>{{loop.decision}}</c> equals <c>done</c> or <c>abort</c>. The engine re-evaluates each var's
/// <c>update</c> against the just-finished body scope at pass-end (so the coordinator's <c>json</c> output is
/// in scope), and the termination set against the freshly-updated <c>{{loop.*}}</c> — exactly the loop's
/// documented contract (<c>LoopConfig</c> / <c>WorkflowEngine.ApplyLoopVarUpdates</c>).</para>
///
/// <para><b>The map fan-out</b> binds <c>items = {{loop.subtasks}}</c> (the current round's work) and collects
/// per-element results under its default <c>results</c> key — the coordinator folds
/// <c>{{nodes.map.outputs.results}}</c> into its prompt to judge the round. The body-node-type switch
/// (<see cref="WorkflowPlanProjector.ResolveBodyTypeKey"/>) is shared with the one-shot projector.</para>
///
/// <para><b>ask_human-in-loop is DEFERRED</b> (see the schema's <c>ask_human</c> enum value, kept for the
/// contract). Adding a chat.post_message + flow.wait_action branch INSIDE the loop body materially enlarges
/// the surface (a second in-body terminal + a conditional branch + the wait wiring), and done/rework/abort
/// already prove the L3 re-decide loop end-to-end. Shipped done/rework/abort first.</para>
/// PR-D.5 follow-up: ask_human-in-loop (coordinator decides ask_human → chat.post_message + flow.wait_action → continue the round).
///
/// <para><b>Known L3 limitations (proper home is PR-E's typed <c>stop</c> verb), documented not papered over:</b>
/// (1) An <c>abort</c> decision terminates the loop and the run still flows synth → Done, so the RUN STATUS is
/// Success even on abort — <c>builtin.terminal</c> is a pure success marker with no failure outcome, and giving
/// abort a distinct Failure status would need an engine change (out of scope for this zero-new-engine-code L3
/// template). The abort IS observable: it's on <c>{{nodes.loop.outputs.decision}}</c> and stated in the
/// synthesizer's closing summary. PR-E's <c>stop{outcome: success|failed|abandoned}</c> gives outcome-typed
/// termination natively. (2) The coordinated end-to-end integration covers the <c>llm.complete</c> body across
/// rounds; the <c>agent.run</c> body that suspends per branch across rounds is covered by COMPOSITION — the
/// coding projection's validity is unit-pinned (both kinds) and agent.run-in-map runnability by PR-D2's
/// headline real-agent E2E — rather than a dedicated coordinated-coding E2E here.</para>
/// </summary>
public sealed partial class WorkflowPlanProjector
{
    public WorkflowDefinition ProjectCoordinated(PlannedWorkflow plan, CoordinationOptions options)
    {
        var bodyTypeKey = ResolveBodyTypeKey(plan);

        return new WorkflowDefinition
        {
            Inputs = new[] { SubtasksInput(plan, HarnessKinds()) },
            Nodes = BuildCoordinatedNodes(plan, bodyTypeKey, options),
            Edges = BuildCoordinatedEdges(),
        };
    }

    private static IReadOnlyList<NodeDefinition> BuildCoordinatedNodes(PlannedWorkflow plan, string bodyTypeKey, CoordinationOptions options) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "approve", TypeKey = "flow.wait_approval", Label = "Review plan",
                Config = Json($$"""{ "prompt": {{JsonString(BuildApprovalPrompt(plan))}} }"""), Inputs = Empty() },

        new() { Id = "gate", TypeKey = "logic.if", Label = "Approved?",
                Config = Json("""{ "condition": "{{nodes.approve.outputs.approved}} == true" }"""), Inputs = Empty() },

        // The coordination loop: bounded rounds, re-seeded each round from the coordinator's decision.
        new() { Id = "loop", TypeKey = "flow.loop", Label = "Coordination rounds",
                Config = CoordinationLoopConfig(options), Inputs = Empty() },
        new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = Empty(), Inputs = Empty() },

        // The round's fan-out: one branch per current-round subtask. items binds the loop var (round 1 = baked
        // plan; later rounds = the coordinator's reworkSubtasks via the loop var update).
        new() { Id = "map", TypeKey = "flow.map", Label = "Run each subtask", ParentId = "loop",
                Config = MapConfig(options), Inputs = Json("""{ "items": "{{loop.subtasks}}" }""") },
        new() { Id = "map_start", TypeKey = "flow.map_start", ParentId = "map", Config = Empty(), Inputs = Empty() },
        // Coordinated runs the platform-default harness (perItemAllocation: false): rework rounds re-seed from the
        // coordinator's reworkSubtasks, which carry no per-item harness, so {{item.harness}} would resolve empty and
        // trip the agent.run guard. Per-subtask Auto-allocation is the one-shot path's job.
        BodyNode(bodyTypeKey, perItemAllocation: false),

        // The coordinator judges the round + decides. Structured output (CoordinatorSchema) lands on `json`;
        // the loop reads json.decision (termination) + json.reworkSubtasks (next round) off it at pass-end.
        new() { Id = "coordinator", TypeKey = "llm.complete", Label = "Coordinator", ParentId = "loop",
                Config = CoordinatorConfig(),
                Inputs = Json($$"""{ "systemPrompt": {{JsonString(CoordinatorSystemPrompt)}}, "userPrompt": {{JsonString(CoordinatorUserPrompt(plan))}} }""") },

        new() { Id = "synth", TypeKey = "llm.complete", Label = "Synthesize results",
                Config = Json("""{ "provider": "Anthropic" }"""),
                Inputs = Json("""{ "systemPrompt": "Summarize the coordinated run's outcome in one concise paragraph.", "userPrompt": "Final coordinator decision: {{nodes.loop.outputs.decision}} after {{nodes.loop.outputs.iterations}} round(s). Write the closing summary." }""") },

        new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(), Inputs = Empty() },
        new() { Id = "rejected", TypeKey = "builtin.terminal", Label = "Rejected", Config = Empty(), Inputs = Empty() },
    };

    private static IReadOnlyList<EdgeDefinition> BuildCoordinatedEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "approve" },
        new() { From = "approve", To = "gate" },
        new() { From = "gate", To = "loop", SourceHandle = "true" },
        new() { From = "gate", To = "rejected", SourceHandle = "false" },
        new() { From = "loop", To = "synth" },
        new() { From = "synth", To = "done" },

        // Loop body sub-DAG: loop_start → map → coordinator (the round, then the verdict).
        new() { From = "ls", To = "map" },
        new() { From = "map", To = "coordinator" },

        // Map body: map_start → single terminal body node (per-element result).
        new() { From = "map_start", To = "body" },
    };

    /// <summary>
    /// The loop config: the two loop variables that thread round state + the termination set. <c>subtasks</c>
    /// seeds round 1 from the baked <c>{{input.subtasks}}</c> and re-seeds from the coordinator's
    /// <c>reworkSubtasks</c>; <c>decision</c> starts <c>"rework"</c> and updates from the coordinator's
    /// <c>decision</c>. Terminate (logic <c>or</c>) on <c>done</c> / <c>abort</c>. maxIterations = MaxRounds.
    /// Plain raw string + Replace keeps the literal <c>{{...}}</c> templates intact (a <c>$$"""…"""</c> string
    /// would mis-read the doubled braces).
    /// </summary>
    private static JsonElement CoordinationLoopConfig(CoordinationOptions options) => Json("""
        {
          "loopVariables": [
            { "name": "subtasks", "type": "Array",  "ref": "{{input.subtasks}}", "update": "{{nodes.coordinator.outputs.json.reworkSubtasks}}" },
            { "name": "decision", "type": "String", "value": "rework",           "update": "{{nodes.coordinator.outputs.json.decision}}" }
          ],
          "termination": {
            "logic": "or",
            "conditions": [
              { "ref": "{{loop.decision}}", "op": "eq", "value": "done" },
              { "ref": "{{loop.decision}}", "op": "eq", "value": "abort" }
            ]
          },
          "maxIterations": __MAX_ROUNDS__
        }
        """.Replace("__MAX_ROUNDS__", Math.Max(1, options.MaxRounds).ToString()));

    /// <summary>The map config — the default <c>results</c> resultKey (the coordinator reads <c>{{nodes.map.outputs.results}}</c>), plus an optional per-round parallelism cap.</summary>
    private static JsonElement MapConfig(CoordinationOptions options) =>
        options.MaxParallelism is { } mp
            ? Json($$"""{ "maxParallelism": {{Math.Clamp(mp, 1, 64)}} }""")
            : Empty();

    /// <summary>The coordinator's <c>llm.complete</c> config: Anthropic + the structured response schema (its decision lands on the <c>json</c> output the loop reads).</summary>
    private static JsonElement CoordinatorConfig() => JsonSerializer.SerializeToElement(new
    {
        provider = "Anthropic",
        responseSchema = CoordinatorSchema.ResponseSchema,
    });

    private const string CoordinatorSystemPrompt =
        "You are the round coordinator for a multi-round workflow. After each round of parallel work, judge the results against the goal and decide what happens next. " +
        "Return 'done' when the goal is met, 'rework' (with reworkSubtasks) when another round would help, or 'abort' when continuing is futile. " +
        "When you choose 'rework', reworkSubtasks must use the same {id,title,instruction} shape as the original subtasks.";

    /// <summary>The coordinator's user prompt: folds the goal + the round's map results so the model judges + decides. The round number isn't a value the loop exposes by ref, so the prompt frames it generically (the engine threads {{loop.index}} only inside loop-scope refs, not into a nested node's static prompt).</summary>
    private static string CoordinatorUserPrompt(PlannedWorkflow plan) =>
        $"Goal:\n{plan.Goal}\n\n" +
        "This round's per-subtask results:\n{{nodes.map.outputs.results}}\n\n" +
        "Judge whether the goal is met. Decide: done | rework | abort. If rework, list the next round's reworkSubtasks.";
}
