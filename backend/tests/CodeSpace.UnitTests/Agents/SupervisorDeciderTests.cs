using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E3 real decider (<see cref="LlmSupervisorDecider"/>) + the projector
/// (<see cref="SupervisorDecisionProjector"/>), driven against a DETERMINISTIC fake at the
/// <see cref="IStructuredLLMClient"/> boundary (the honest seam — only the network call is replaced). Pins:
/// the decider folds the turn context into a prompt + projects a schema-valid model decision into a canonical
/// <see cref="SupervisorDecision"/>; each verb projects to its canonical payload; a missing model + an unknown
/// kind both FAIL CLOSED to a terminal stop (no crash).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDeciderTests
{
    private static SupervisorTurnContext Context(int turnNumber = 0, params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship the feature", TurnNumber = turnNumber, PriorDecisions = prior };

    // ── The decider folds context → a schema-valid canonical decision ────────────────

    [Fact]
    public async Task The_decider_projects_a_plan_model_decision_into_a_canonical_plan()
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Plan,
            Plan = new SupervisorPlanPayload
            {
                Goal = "ship",
                Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" } },
            },
        };

        var decision = await Decider(model).DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.IsTerminal.ShouldBeFalse();

        var subtasks = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtasks");
        subtasks.GetArrayLength().ShouldBe(1);
        subtasks[0].GetProperty("id").GetString().ShouldBe("s1");
    }

    [Fact]
    public async Task The_user_prompt_folds_goal_turn_and_prior_decisions()
    {
        var prior = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtasks":[]}""", OutcomeJson = """{"planned":[]}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 1, prior));

        prompt.ShouldContain("ship the feature", Case.Insensitive);
        prompt.ShouldContain("Turn: 1");
        prompt.ShouldContain(SupervisorDecisionKinds.Plan, Case.Insensitive, "the prior plan is folded into the prompt so the decider can spawn over it");
    }

    [Fact]
    public async Task A_deployment_with_no_structured_provider_fails_closed_to_a_terminal_stop()
    {
        var decider = new LlmSupervisorDecider(new FakeRegistry(structured: null));

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("no model → a clean one-turn no-op stop, never a crash");
    }

    // ── The projector maps each verb to its canonical payload ────────────────────────

    [Fact]
    public void Spawn_projects_the_subtask_ids()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1", "s2" } },
        });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtaskIds").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void Stop_is_terminal_and_retry_merge_ask_human_are_not()
    {
        Project(SupervisorDecisionKinds.Stop, m => m with { Stop = new SupervisorStopPayload { Outcome = "done" } }).IsTerminal.ShouldBeTrue();
        Project(SupervisorDecisionKinds.Retry, m => m with { Retry = new SupervisorRetryPayload { SubtaskId = "s1" } }).IsTerminal.ShouldBeFalse();
        Project(SupervisorDecisionKinds.Merge, m => m with { Merge = new SupervisorMergePayload() }).IsTerminal.ShouldBeFalse();
        Project(SupervisorDecisionKinds.AskHuman, m => m with { AskHuman = new SupervisorAskHumanPayload { Question = "?" } }).IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_kind_projects_to_a_terminal_stop()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = "wat" });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("an unrecognized verb fails closed to a terminal stop");
    }

    [Fact]
    public void Projection_is_deterministic_in_the_model_decision()
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "a" } } };

        SupervisorDecisionProjector.Project(model).PayloadJson.ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson, "same model decision → byte-identical canonical payload (the idempotency-key stability the ledger relies on)");
    }

    private static SupervisorDecision Project(string kind, Func<SupervisorModelDecision, SupervisorModelDecision> fill) =>
        SupervisorDecisionProjector.Project(fill(new SupervisorModelDecision { Kind = kind }));

    private static LlmSupervisorDecider Decider(SupervisorModelDecision model) =>
        new(new FakeRegistry(new FakeStructuredClient(model)));

    // ── Fakes at the honest IStructuredLLMClient seam ────────────────────────────────

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(IStructuredLLMClient? structured) =>
            All = structured == null ? Array.Empty<ILLMClient>() : new ILLMClient[] { (ILLMClient)structured };

        public IReadOnlyList<ILLMClient> All { get; }

        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class FakeStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly SupervisorModelDecision _model;

        public FakeStructuredClient(SupervisorModelDecision model) => _model = model;

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(_model, SupervisorDecisionSchema.Options), Model = request.Model });
    }
}
