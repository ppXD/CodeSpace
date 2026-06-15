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
            SupervisorScriptMode.AskHumanStop => AskHumanStop(context),
            SupervisorScriptMode.PlanThenSpawnForever => PlanThenSpawnForever(context),
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

    public void AskHumanStop() => Mode = SupervisorScriptMode.AskHumanStop;

    public void PlanThenSpawnForever() => Mode = SupervisorScriptMode.PlanThenSpawnForever;
}

public enum SupervisorScriptMode
{
    PlanThenStop,
    PlanSpawnStop,
    AskHumanStop,
    PlanThenSpawnForever,
}
