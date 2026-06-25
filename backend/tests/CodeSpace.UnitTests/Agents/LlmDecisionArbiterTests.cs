using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Unit-pins the <see cref="LlmDecisionArbiter"/>'s OWN logic — the projection (fail-closed to escalate), the
/// decision→prompt framing, and the end-to-end <see cref="LlmDecisionArbiter.DecideAsync"/> over fakes at the
/// <see cref="IStructuredLLMClient"/> seam (no LLM, no DB). The DRAIN that actuates a verdict is covered separately
/// (<c>SupervisorArbiterDrainTests</c> / <c>SupervisorArbiterDrainFlowTests</c>); this is the arbiter brain itself.
/// </summary>
public sealed class LlmDecisionArbiterTests
{
    // ── Project: fail-closed to escalate on anything but a clean answer ───────────────

    [Fact]
    public void An_answer_kind_projects_to_an_answer_verdict_carrying_the_options_and_rationale()
    {
        var verdict = LlmDecisionArbiter.Project(new ArbiterModelDecision
        {
            Kind = "answer",
            Answer = new ArbiterModelAnswer { SelectedOptions = new[] { "a" }, FreeText = null },
            Rationale = "low-risk + recommended",
        });

        verdict.IsAnswer.ShouldBeTrue();
        verdict.SelectedOptions.ShouldBe(new[] { "a" });
        verdict.Rationale.ShouldBe("low-risk + recommended");
    }

    [Fact]
    public void A_free_text_answer_projects_through()
    {
        var verdict = LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "answer", Answer = new ArbiterModelAnswer { FreeText = "use UTC" }, Rationale = "obvious" });

        verdict.IsAnswer.ShouldBeTrue();
        verdict.FreeText.ShouldBe("use UTC");
    }

    [Theory]
    [InlineData("escalate")]   // an explicit escalation
    [InlineData("ANSWERED")]   // an unknown verb fails closed
    [InlineData("")]           // a blank kind fails closed
    [InlineData(null)]         // a missing kind fails closed
    public void Anything_but_a_clean_answer_projects_to_escalate(string? kind)
    {
        var verdict = LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = kind, Rationale = "unsure" });

        verdict.IsAnswer.ShouldBeFalse("only a clean 'answer' kind answers — everything else escalates to a human (the safe default)");
        verdict.Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public void A_blank_rationale_degrades_to_a_placeholder_never_silent()
    {
        LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "answer", Answer = new ArbiterModelAnswer { SelectedOptions = new[] { "a" } }, Rationale = "  " })
            .Rationale.ShouldBe("(the arbiter gave no rationale)");
    }

    [Fact]
    public void An_answer_with_no_answer_object_projects_to_an_empty_option_list_not_a_crash()
    {
        var verdict = LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "answer", Answer = null, Rationale = "x" });

        verdict.IsAnswer.ShouldBeTrue();
        verdict.SelectedOptions.ShouldBeEmpty();
    }

    // ── BuildUserPrompt: the decision is framed legibly for the brain ─────────────────

    [Fact]
    public void The_prompt_frames_the_goal_question_risk_recommendation_and_options()
    {
        var decision = Pending("pick a migration path", risk: DecisionRiskLevels.Low, recommended: "a",
            blocking: "the agent is blocked on the schema choice",
            options: new[] { new DecisionOption { Id = "a", Label = "additive" }, new DecisionOption { Id = "b", Label = "destructive", IsSideEffecting = true } });

        var prompt = LlmDecisionArbiter.BuildUserPromptForTest(decision, "ship the feature");

        prompt.ShouldContain("Goal: ship the feature");
        prompt.ShouldContain("pick a migration path");
        prompt.ShouldContain("the agent is blocked on the schema choice");
        prompt.ShouldContain(DecisionRiskLevels.Low);
        prompt.ShouldContain("Recommended option: a");
        prompt.ShouldContain("a: additive");
        prompt.ShouldContain("b: destructive (irreversible)", customMessage: "an irreversible option is marked so the brain weighs it");
    }

    // ── DecideAsync end-to-end over fakes: fail-closed everywhere but a clean answer ──

    [Fact]
    public async Task A_run_with_no_brain_model_escalates_without_calling_the_model()
    {
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeArbiterClient(Answer("a"))), FakeSelector.WithModel());

        var verdict = await arbiter.DecideAsync(Pending("x"), TeamId, supervisorModelId: null, "goal", CancellationToken.None);

        verdict.IsAnswer.ShouldBeFalse("no brain model → escalate to a human, the safe default");
    }

    [Fact]
    public async Task A_clean_answer_from_the_model_is_answered()
    {
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeArbiterClient(Answer("a"))), FakeSelector.WithModel());

        var verdict = await arbiter.DecideAsync(Pending("x"), TeamId, Guid.NewGuid(), "goal", CancellationToken.None);

        verdict.IsAnswer.ShouldBeTrue();
        verdict.SelectedOptions.ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task An_empty_pool_escalates()
    {
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeArbiterClient(Answer("a"))), FakeSelector.Empty());

        (await arbiter.DecideAsync(Pending("x"), TeamId, Guid.NewGuid(), "goal", CancellationToken.None)).IsAnswer.ShouldBeFalse();
    }

    [Fact]
    public async Task A_throwing_model_call_escalates_never_crashes_the_turn()
    {
        // The arbiter's caller relies on ALWAYS getting a verdict — a transient API error / malformed reply must fail
        // closed to a human escalation, never throw out of the drain.
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new ThrowingArbiterClient()), FakeSelector.WithModel());

        var verdict = await arbiter.DecideAsync(Pending("x"), TeamId, Guid.NewGuid(), "goal", CancellationToken.None);

        verdict.IsAnswer.ShouldBeFalse("a thrown brain call escalates to a human");
        verdict.Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task Cancellation_propagates_it_is_not_swallowed_into_an_escalation()
    {
        // A torn-down run (cancellation) is NOT an escalation — there is no human to escalate to; the cancel propagates.
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new ThrowingArbiterClient(cancel: true)), FakeSelector.WithModel());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            arbiter.DecideAsync(Pending("x"), TeamId, Guid.NewGuid(), "goal", CancellationToken.None));
    }

    // ── Helpers + fakes at the honest IStructuredLLMClient / IModelPoolSelector seams ─

    private static readonly Guid TeamId = Guid.NewGuid();

    private static object Answer(string option) => new { kind = "answer", answer = new { selectedOptions = new[] { option } }, rationale = "low-risk + recommended" };

    private static PendingDecision Pending(string question, string risk = "low", string? recommended = "a", string? blocking = "blocked", IReadOnlyList<DecisionOption>? options = null) => new()
    {
        Id = Guid.NewGuid(),
        Grain = DecisionResumeBackends.ToolLedger,
        RootTraceId = Guid.NewGuid(),
        DecisionType = DecisionTypes.ChooseOne,
        Question = question,
        Options = options ?? new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B" } },
        RecommendedOption = recommended,
        BlockingReason = blocking,
        RiskLevel = risk,
        Policy = DecisionPolicies.SupervisorFirst,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(IStructuredLLMClient structured) => All = new ILLMClient[] { (ILLMClient)structured };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class FakeArbiterClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _json;
        public FakeArbiterClient(object verdict) => _json = JsonSerializer.SerializeToElement(verdict);

        public string Provider => "TestArbiter";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
    }

    private sealed class ThrowingArbiterClient : ILLMClient, IStructuredLLMClient
    {
        private readonly bool _cancel;
        public ThrowingArbiterClient(bool cancel = false) => _cancel = cancel;

        public string Provider => "TestArbiter";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            _cancel ? throw new OperationCanceledException() : throw new LlmApiException("TestArbiter", null, LlmErrorCategory.Transient, "boom");
    }

    private sealed class FakeSelector : IModelPoolSelector
    {
        private readonly ModelPoolPick? _pick;
        private FakeSelector(ModelPoolPick? pick) => _pick = pick;

        public static FakeSelector WithModel() => new(new ModelPoolPick { ModelId = "m", Credential = new ResolvedModelCredential { Provider = "TestArbiter", ApiKey = "sk-test" } });
        public static FakeSelector Empty() => new(null);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult(_pick);
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult(_pick);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }
}
