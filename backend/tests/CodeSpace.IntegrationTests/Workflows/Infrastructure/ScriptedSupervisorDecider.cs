using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A DETERMINISTIC, test-controllable <see cref="ISupervisorDecider"/> for the supervisor integration tests —
/// the model-free seam that drives the REAL <c>SupervisorTurnService</c> + <c>RealSupervisorActionExecutor</c>
/// with no LLM, so the dual-resume-path + the spawn barrier are pinned deterministically. Registered at the
/// fixture root OVER the production <c>LlmSupervisorDecider</c> (last-wins), so the supervisor node's own DI
/// scope resolves THIS, not the LLM decider. Per-turn behaviour is read from the fixture-singleton
/// <see cref="SupervisorDecisionScript"/> a test sets — default <see cref="SupervisorDecisionScript.PlanThenStop"/>
/// keeps the E2 plan→stop flow green; the E3 crown-jewel sets <see cref="SupervisorDecisionScript.PlanSpawnStop"/>.
/// </summary>
public sealed class ScriptedSupervisorDecider : ISupervisorDecider
{
    public const string SubtaskA = "sa";
    public const string SubtaskB = "sb";

    private readonly SupervisorDecisionScript _script;

    public ScriptedSupervisorDecider(SupervisorDecisionScript script) { _script = script; }

    /// <summary>The question the AskHumanStop arc asks at turn 0 — the integration test asserts the posted card body + the recorded outcome carry it.</summary>
    public const string AskQuestion = "which approach: rewrite or patch?";

    public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var decision = _script.Mode switch
        {
            SupervisorScriptMode.PlanSpawnStop => PlanSpawnStop(context),
            SupervisorScriptMode.PlanSpawnMergeStop => PlanSpawnMergeStop(context),
            SupervisorScriptMode.PlanSpawnSingleMergeStop => PlanSpawnSingleMergeStop(context),
            SupervisorScriptMode.PlanSpawnRetryMergeStop => PlanSpawnRetryMergeStop(context),
            SupervisorScriptMode.PlanSpawnMergeResolveMergeStop => PlanSpawnMergeResolveMergeStop(context),
            SupervisorScriptMode.PlanSpawnMergeResolveApprovedMergeStop => PlanSpawnMergeResolveApprovedMergeStop(context),
            SupervisorScriptMode.AskHumanStop => AskHumanStop(context),
            SupervisorScriptMode.PlanThenSpawnForever => PlanThenSpawnForever(context),
            SupervisorScriptMode.PlanSpawnDispatchStop => PlanSpawnDispatchStop(context),
            SupervisorScriptMode.PlanSpawnBadRepoStop => PlanSpawnBadRepoStop(context),
            SupervisorScriptMode.PlanSpawnBadModelStop => PlanSpawnBadModelStop(context),
            SupervisorScriptMode.PlanSpawnPersonaStop => PlanSpawnPersonaStop(context),
            SupervisorScriptMode.PlanSpawnBadPersonaStop => PlanSpawnBadPersonaStop(context),
            _ => PlanThenStop(context),
        };

        return Task.FromResult(decision);
    }

    // E5 arc: turn 0 plan(2) → EVERY later turn spawn(both). The decider NEVER stops on its own — it's the
    // BOUND (total-spawn cap) or the GOVERNANCE gate that must stop it, so the integration test proves the
    // fail-closed force-STOP / approval-park, not a cooperative decider.
    private static SupervisorDecision PlanThenSpawnForever(SupervisorTurnContext context) => context.TurnNumber == 0
        ? Plan(context.Goal)
        : Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA, SubtaskB } });

    // E4 arc: turn 0 ask_human("which approach?") → turn 1 stop, but the stop's summary ECHOES the folded human
    // answer the decider reads off the prior ask_human decision's outcome — proving the answer reached the next
    // turn's context. A turn-0 re-entry (before the human answers) re-emits the SAME ask_human (idempotent key).
    private static SupervisorDecision AskHumanStop(SupervisorTurnContext context)
    {
        if (context.TurnNumber == 0)
            return Canonical(SupervisorDecisionKinds.AskHuman, new SupervisorAskHumanPayload { Question = AskQuestion });

        var priorAsk = context.PriorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman);
        var answer = SupervisorOutcome.ReadAskHumanAnswer(priorAsk?.OutcomeJson) ?? "<no answer folded>";

        return Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = $"human said: {answer}" });
    }

    // E2 arc: turn 0 plan → every later turn stop. The plan still records 2 subtasks (legible replay tape).
    private static SupervisorDecision PlanThenStop(SupervisorTurnContext context) => context.TurnNumber == 0
        ? Plan(context.Goal)
        : Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "plan complete" });

    // E3 arc: turn 0 plan(2) → turn 1 spawn(both) → turn 2 stop. The spawn references the plan's subtask ids.
    private static SupervisorDecision PlanSpawnStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA, SubtaskB } }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "both subtasks done" }),
    };

    // Merge arc: turn 0 plan(2) → turn 1 spawn(both) → turn 2 merge → turn 3 stop. The merge folds the prior
    // spawn's agent results (the executor resolves the staged ids itself), proving the full-contribution fold.
    private static SupervisorDecision PlanSpawnMergeStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA, SubtaskB } }),
        2 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "combine both branches" }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "merged" }),
    };

    // Single-agent merge arc: turn 0 plan(2) → turn 1 spawn ONLY SubtaskA → turn 2 merge (one branch, no conflict) →
    // turn 3 stop. Used by the goal-relevance oracle test: ONE agent edits solution.sh, so the integrated head is that
    // agent's edit alone and the check.sh oracle grades exactly its correctness (two agents both editing solution.sh
    // would conflict and obscure the signal).
    private static SupervisorDecision PlanSpawnSingleMergeStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA } }),
        2 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "integrate the solution" }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "solution integrated" }),
    };

    // Failure→retry arc: turn 0 plan(2) → turn 1 spawn(both) → the "do beta" subtask FAILS in FailFirstThenSucceedFakeCli
    // → turn 2 RETRY it with a revised instruction carrying the "retry" marker (which the fake CLI succeeds on) → turn 3
    // merge (folds the retry's patch alongside the alpha patch) → turn 4 stop. Proves real failure-recovery through the
    // real engine: a failed agent run is a SIGNAL the decider recovers from, not a run-killer.
    private static SupervisorDecision PlanSpawnRetryMergeStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA, SubtaskB } }),
        2 => Canonical(SupervisorDecisionKinds.Retry, new SupervisorRetryPayload { SubtaskId = SubtaskB, RevisedInstruction = "do beta retry" }),
        3 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "combine both branches" }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "recovered via retry" }),
    };

    // Conflict→resolve arc: plan(2) → spawn(both, editing the SAME file with conflicting content) → merge (CONFLICTED:
    // real git can't auto-combine the two edits) → resolve (a resolver agent reconciles + verifies) → merge (accepts the
    // VERIFIED resolution's branch as the integrated head) → stop. Proves the full conflict-recovery loop through real git.
    private static SupervisorDecision PlanSpawnMergeResolveMergeStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA, SubtaskB } }),
        2 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "combine both branches" }),
        3 => Canonical(SupervisorDecisionKinds.Resolve, new SupervisorResolvePayload()),
        4 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "accept the reconciled result" }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "conflict reconciled" }),
    };

    // Conflict→resolve FULL loop (slice C-full): plan(2) → spawn(both, conflicting) → merge (CONFLICTED) → resolve
    // (turn 3, irreversible → gated into an ask_human APPROVAL card that parks the run) → [human approves] → resolve
    // RE-EMITTED (turn 4 — WasJustApproved now fires, so the turn service EXECUTES it: a resolver agent reconciles +
    // verifies) → merge (turn 5 — accepts the VERIFIED resolution's branch as the integrated head) → stop. Proves the
    // whole conflict-recovery loop INCLUDING the human approval of the irreversible re-merge.
    private static SupervisorDecision PlanSpawnMergeResolveApprovedMergeStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload { SubtaskIds = new[] { SubtaskA, SubtaskB } }),
        2 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "combine both branches" }),
        3 => Canonical(SupervisorDecisionKinds.Resolve, new SupervisorResolvePayload()),   // → ask_human approval (parks)
        4 => Canonical(SupervisorDecisionKinds.Resolve, new SupervisorResolvePayload()),   // re-emit; WasJustApproved → executes the resolver
        5 => Canonical(SupervisorDecisionKinds.Merge, new SupervisorMergePayload { SynthesisInstruction = "accept the reconciled result" }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "conflict reconciled + accepted" }),
    };

    /// <summary>The guid a bad dispatch targets — never bound by any test profile, so the per-agent repo clamp rejects it.</summary>
    public static readonly Guid UnboundRepo = Guid.Parse("dead0000-0000-0000-0000-00000000beef");

    // L4 arc B: turn 0 plan(2) → turn 1 spawn with PER-AGENT dispatch (each subtask a distinct role + override) → turn 2
    // stop. Proves one spawn fans out two agents with DIFFERENT model-authored shapes.
    private static SupervisorDecision PlanSpawnDispatchStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload
        {
            SubtaskIds = new[] { SubtaskA, SubtaskB },
            Agents = new[]
            {
                new SupervisorAgentDispatch { SubtaskId = SubtaskA, Role = "backend implementer", Harness = "claude-code", AutonomyLevel = "confined" },
                new SupervisorAgentDispatch { SubtaskId = SubtaskB, Role = "frontend adapter" },
            },
        }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "dispatched" }),
    };

    // L4 arc B: a spawn whose dispatch targets a repo the operator did NOT bind → the per-agent repo clamp throws, and
    // the turn service must terminalize the spawn as a CLEAN failure (no stranded-Running decision, no crash).
    private static SupervisorDecision PlanSpawnBadRepoStop(SupervisorTurnContext context) => context.TurnNumber == 0
        ? Plan(context.Goal)
        : Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload
        {
            SubtaskIds = new[] { SubtaskA },
            Agents = new[]
            {
                new SupervisorAgentDispatch { SubtaskId = SubtaskA, TargetRepos = JsonSerializer.SerializeToElement(new[] { new { repositoryId = UnboundRepo } }) },
            },
        });

    // S4: a spawn whose dispatch authors a model OUTSIDE the operator's allowed pool → the per-agent model clamp throws,
    // and the turn service must terminalize the spawn as a CLEAN failure (no stranded-Running decision, no agent staged).
    private static SupervisorDecision PlanSpawnBadModelStop(SupervisorTurnContext context) => context.TurnNumber == 0
        ? Plan(context.Goal)
        : Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload
        {
            SubtaskIds = new[] { SubtaskA },
            Agents = new[]
            {
                new SupervisorAgentDispatch { SubtaskId = SubtaskA, Model = "rogue-model" },
            },
        });

    /// <summary>The slug a persona dispatch targets — the test seeds a persona with THIS slug, so the per-agent persona resolves to it.</summary>
    public const string DispatchPersonaSlug = "dispatch-persona";

    /// <summary>A persona slug NO test seeds → the per-agent persona resolution fails closed (a clean terminal).</summary>
    public const string MissingPersonaSlug = "ghost-persona";

    // P3: turn 0 plan(2) → turn 1 spawn ONE agent whose dispatch authors a per-agent PERSONA slug → turn 2 stop. Proves
    // the model-authored persona resolves to the team AgentDefinitionId and merges (overriding the run-level profile persona).
    private static SupervisorDecision PlanSpawnPersonaStop(SupervisorTurnContext context) => context.TurnNumber switch
    {
        0 => Plan(context.Goal),
        1 => Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload
        {
            SubtaskIds = new[] { SubtaskA },
            Agents = new[] { new SupervisorAgentDispatch { SubtaskId = SubtaskA, AgentDefinition = DispatchPersonaSlug } },
        }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "completed", Summary = "persona dispatched" }),
    };

    // P3: a spawn whose dispatch authors a persona slug NO active team persona has → the per-agent persona clamp throws,
    // and the turn service must terminalize the spawn as a CLEAN failure (no stranded-Running decision, no agent staged).
    private static SupervisorDecision PlanSpawnBadPersonaStop(SupervisorTurnContext context) => context.TurnNumber == 0
        ? Plan(context.Goal)
        : Canonical(SupervisorDecisionKinds.Spawn, new SupervisorSpawnPayload
        {
            SubtaskIds = new[] { SubtaskA },
            Agents = new[] { new SupervisorAgentDispatch { SubtaskId = SubtaskA, AgentDefinition = MissingPersonaSlug } },
        });

    private static SupervisorDecision Plan(string goal) => Canonical(SupervisorDecisionKinds.Plan, new SupervisorPlanPayload
    {
        Goal = goal,
        Subtasks = new[]
        {
            new SupervisorPlannedSubtask { Id = SubtaskA, Title = "Alpha", Instruction = "do alpha" },
            new SupervisorPlannedSubtask { Id = SubtaskB, Title = "Beta", Instruction = "do beta" },
        },
    });

    private static SupervisorDecision Canonical<TPayload>(string kind, TPayload payload) => new()
    {
        Kind = kind,
        PayloadJson = JsonSerializer.Serialize(payload, AgentJson.Options),
    };
}

/// <summary>The fixture-singleton script knob the <see cref="ScriptedSupervisorDecider"/> reads. A test mutates <see cref="Mode"/> before driving the engine; default is the E2 plan→stop arc.</summary>
public sealed class SupervisorDecisionScript
{
    public SupervisorScriptMode Mode { get; set; } = SupervisorScriptMode.PlanThenStop;

    public void PlanThenStop() => Mode = SupervisorScriptMode.PlanThenStop;

    public void PlanSpawnStop() => Mode = SupervisorScriptMode.PlanSpawnStop;

    public void PlanSpawnMergeStop() => Mode = SupervisorScriptMode.PlanSpawnMergeStop;

    public void PlanSpawnSingleMergeStop() => Mode = SupervisorScriptMode.PlanSpawnSingleMergeStop;

    public void PlanSpawnRetryMergeStop() => Mode = SupervisorScriptMode.PlanSpawnRetryMergeStop;

    public void PlanSpawnMergeResolveMergeStop() => Mode = SupervisorScriptMode.PlanSpawnMergeResolveMergeStop;

    public void PlanSpawnMergeResolveApprovedMergeStop() => Mode = SupervisorScriptMode.PlanSpawnMergeResolveApprovedMergeStop;

    public void AskHumanStop() => Mode = SupervisorScriptMode.AskHumanStop;

    public void PlanThenSpawnForever() => Mode = SupervisorScriptMode.PlanThenSpawnForever;

    public void PlanSpawnDispatchStop() => Mode = SupervisorScriptMode.PlanSpawnDispatchStop;

    public void PlanSpawnBadRepoStop() => Mode = SupervisorScriptMode.PlanSpawnBadRepoStop;

    public void PlanSpawnBadModelStop() => Mode = SupervisorScriptMode.PlanSpawnBadModelStop;

    public void PlanSpawnPersonaStop() => Mode = SupervisorScriptMode.PlanSpawnPersonaStop;

    public void PlanSpawnBadPersonaStop() => Mode = SupervisorScriptMode.PlanSpawnBadPersonaStop;
}

/// <summary>
/// The fixture-singleton knob that picks WHICH <see cref="CodeSpace.Core.Services.Supervisor.ISupervisorDecider"/> the
/// engine resolves: the deterministic <see cref="ScriptedSupervisorDecider"/> (default) or the production
/// <see cref="CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider"/> — so a <c>[Trait RealModel]</c> test
/// can drive the REAL durable engine with a LIVE brain (its credential resolved from a seeded DB row) instead of a
/// scripted one. Defaults to <see cref="UseLiveModel"/> = false, so every existing supervisor test is byte-identical;
/// a test that flips it MUST reset it in Dispose (the fixture is shared across the whole Postgres collection).
/// </summary>
public sealed class SupervisorDeciderMode
{
    public bool UseLiveModel { get; set; }
}

public enum SupervisorScriptMode
{
    PlanThenStop,
    PlanSpawnStop,
    PlanSpawnMergeStop,
    PlanSpawnSingleMergeStop,
    PlanSpawnRetryMergeStop,
    PlanSpawnMergeResolveMergeStop,
    PlanSpawnMergeResolveApprovedMergeStop,
    PlanSpawnDispatchStop,
    PlanSpawnBadRepoStop,
    PlanSpawnBadModelStop,
    PlanSpawnPersonaStop,
    PlanSpawnBadPersonaStop,
    AskHumanStop,
    PlanThenSpawnForever,
}
