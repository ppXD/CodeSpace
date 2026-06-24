using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// 🟢 Unit: the decision arbiter brain (<see cref="LlmDecisionArbiter"/>) + its projection, driven against a
/// deterministic fake at the <see cref="IStructuredLLMClient"/> seam (only the network call is replaced). Pins: a clean
/// model verdict projects to answer/escalate; an unknown / missing kind, a missing or unusable brain model, an empty
/// pool, and no structured provider ALL fail closed to ESCALATE (a human decides — never a silent / guessed auto-answer).
/// </summary>
[Trait("Category", "Unit")]
public class DecisionArbiterTests
{
    private static readonly Guid BrainModelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PendingDecision Decision(string risk = "low") => new()
    {
        Id = Guid.NewGuid(),
        Grain = DecisionResumeBackends.ToolLedger,
        RootTraceId = Guid.NewGuid(),
        DecisionType = DecisionTypes.ChooseOne,
        Question = "Which migration path?",
        Options = new[] { new DecisionOption { Id = "a", Label = "Path A" }, new DecisionOption { Id = "b", Label = "Path B" } },
        RecommendedOption = "a",
        BlockingReason = "the schema diverged",
        RiskLevel = risk,
        Policy = DecisionPolicies.SupervisorFirst,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static ArbiterModelDecision Answer() => new() { Kind = "answer", Answer = new ArbiterModelAnswer { SelectedOptions = new[] { "a" } }, Rationale = "recommended + reversible" };

    // ── Projection (fail-closed) ──────────────────────────────────────────────────

    [Fact]
    public void An_answer_model_projects_to_an_answer_verdict()
    {
        var verdict = LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "answer", Answer = new ArbiterModelAnswer { SelectedOptions = new[] { "a" } }, Rationale = "Path A is reversible + recommended" });

        verdict.IsAnswer.ShouldBeTrue();
        verdict.SelectedOptions.ShouldBe(new[] { "a" });
        verdict.Rationale.ShouldBe("Path A is reversible + recommended");
    }

    [Fact]
    public void An_escalate_model_projects_to_an_escalate_verdict() =>
        LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "escalate", Rationale = "too risky to decide" }).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);

    [Theory]
    [InlineData("wat")]
    [InlineData("ANSWER_MAYBE")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_missing_kind_fails_closed_to_escalate(string? kind) =>
        LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = kind, Rationale = "x" }).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);

    [Fact]
    public void A_blank_rationale_degrades_to_a_placeholder_never_silent() =>
        LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "answer", Answer = new ArbiterModelAnswer { SelectedOptions = new[] { "a" } }, Rationale = "   " })
            .Rationale.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void A_free_text_answer_projects_its_text()
    {
        var verdict = LlmDecisionArbiter.Project(new ArbiterModelDecision { Kind = "answer", Answer = new ArbiterModelAnswer { FreeText = "rename to user_id" }, Rationale = "obvious, low-risk" });

        verdict.IsAnswer.ShouldBeTrue();
        verdict.FreeText.ShouldBe("rename to user_id");
        verdict.SelectedOptions.ShouldBeEmpty();
    }

    // ── Fail-closed to ESCALATE (the safe default — unlike the supervisor decider, which stops) ──

    [Fact]
    public async Task No_brain_model_escalates()
    {
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeArbiterClient(Answer())), FakeSelector.WithModel());

        (await arbiter.DecideAsync(Decision(), TeamId, supervisorModelId: null, "goal", CancellationToken.None)).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task An_empty_pool_escalates()
    {
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeArbiterClient(Answer())), FakeSelector.Empty());

        (await arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", CancellationToken.None)).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task No_structured_provider_escalates()
    {
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(structured: null), FakeSelector.WithModel());

        (await arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", CancellationToken.None)).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task A_type_mismatched_verdict_fails_closed_to_escalate()
    {
        // The forced-tool path doesn't hard-validate, so the model CAN emit `answer` as a string — Deserialize THROWS
        // (it does not return null). The arbiter must catch it and escalate, never let it throw past the caller.
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeRawArbiterClient("""{"kind":"answer","answer":"oops not an object","rationale":"x"}""")), FakeSelector.WithModel());

        (await arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", CancellationToken.None)).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task A_throwing_brain_call_fails_closed_to_escalate()
    {
        // A transient API / transport error (429, 500, no-tool-block) throws out of CompleteStructuredAsync — escalate, never throw.
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeThrowingArbiterClient()), FakeSelector.WithModel());

        (await arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", CancellationToken.None)).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task A_null_verdict_fails_closed_to_escalate()
    {
        // The JSON literal `null` is the one shape Deserialize returns null for — the explicit "no decision" safety net.
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeRawArbiterClient("null")), FakeSelector.WithModel());

        (await arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", CancellationToken.None)).Kind.ShouldBe(ArbiterVerdictKinds.Escalate);
    }

    [Fact]
    public async Task Cancellation_propagates_it_is_not_swallowed_as_an_escalation()
    {
        // The run is being torn down — there is no human to escalate to. Cancellation must NOT degrade to a verdict.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(new FakeThrowingArbiterClient(new OperationCanceledException())), FakeSelector.WithModel());

        await Should.ThrowAsync<OperationCanceledException>(() => arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", cts.Token));
    }

    // ── With the brain: the arbiter calls the picked model + projects its verdict ──

    [Fact]
    public async Task The_arbiter_calls_the_picked_model_and_projects_its_answer()
    {
        var fake = new FakeArbiterClient(Answer());
        var arbiter = new LlmDecisionArbiter(new FakeRegistry(fake), FakeSelector.WithModel("claude-opus-4-8"));

        var verdict = await arbiter.DecideAsync(Decision(), TeamId, BrainModelId, "goal", CancellationToken.None);

        verdict.IsAnswer.ShouldBeTrue();
        verdict.SelectedOptions.ShouldBe(new[] { "a" });
        fake.LastModel.ShouldBe("claude-opus-4-8", "the arbiter uses the model the pool selector chose — no default");
    }

    [Fact]
    public void The_prompt_folds_the_decision_question_options_risk_and_recommendation()
    {
        var prompt = LlmDecisionArbiter.BuildUserPromptForTest(Decision(), "ship the migration");

        prompt.ShouldContain("ship the migration");
        prompt.ShouldContain("Which migration path?");
        prompt.ShouldContain("Path A", Case.Insensitive);
        prompt.ShouldContain("Recommended option: a");
        prompt.ShouldContain("Risk: low", Case.Insensitive);
    }

    [Fact]
    public void The_prompt_marks_irreversible_options_and_folds_context_and_blocking_reason()
    {
        var decision = new PendingDecision
        {
            Id = Guid.NewGuid(),
            Grain = DecisionResumeBackends.ToolLedger,
            RootTraceId = Guid.NewGuid(),
            DecisionType = DecisionTypes.ChooseOne,
            Question = "Drop the legacy column?",
            Options = new[] { new DecisionOption { Id = "drop", Label = "Drop it", IsSideEffecting = true }, new DecisionOption { Id = "keep", Label = "Keep it" } },
            BlockingReason = "the column is referenced by a view",
            ContextSummary = "12 rows still populate it",
            RiskLevel = "high",
            Policy = DecisionPolicies.HumanRequired,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var prompt = LlmDecisionArbiter.BuildUserPromptForTest(decision, "clean up the schema");

        prompt.ShouldContain("(irreversible)");
        prompt.ShouldContain("the column is referenced by a view");
        prompt.ShouldContain("12 rows still populate it");
    }

    [Fact]
    public void A_free_text_decision_with_no_options_prompts_for_free_text()
    {
        var decision = new PendingDecision
        {
            Id = Guid.NewGuid(),
            Grain = DecisionResumeBackends.ToolLedger,
            RootTraceId = Guid.NewGuid(),
            DecisionType = DecisionTypes.FreeText,
            Question = "What should the table be named?",
            Options = Array.Empty<DecisionOption>(),
            RiskLevel = "low",
            Policy = DecisionPolicies.SupervisorFirst,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        LlmDecisionArbiter.BuildUserPromptForTest(decision, "name the table").ShouldContain("free-text answer expected");
    }

    [Fact]
    public void The_schema_is_a_closed_object_with_an_answer_or_escalate_kind()
    {
        var root = ArbiterDecisionSchema.ResponseSchema;

        root.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("rationale");
        root.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "answer", "escalate" });
    }

    // ── Fakes at the honest IStructuredLLMClient seam (mirror SupervisorDeciderTests) ──

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(IStructuredLLMClient? structured) =>
            All = structured == null ? Array.Empty<ILLMClient>() : new ILLMClient[] { (ILLMClient)structured };

        public IReadOnlyList<ILLMClient> All { get; }

        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class FakeArbiterClient : ILLMClient, IStructuredLLMClient
    {
        private readonly ArbiterModelDecision _model;
        public string? LastModel;

        public FakeArbiterClient(ArbiterModelDecision model) => _model = model;

        public string Provider => "TestArbiter";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            LastModel = request.Model;
            return Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(_model, ArbiterDecisionSchema.Options), Model = request.Model });
        }
    }

    /// <summary>Returns a verbatim JSON payload — lets a test feed a type-mismatched or null shape the strongly-typed fake cannot emit.</summary>
    private sealed class FakeRawArbiterClient : ILLMClient, IStructuredLLMClient
    {
        private readonly string _rawJson;
        public FakeRawArbiterClient(string rawJson) => _rawJson = rawJson;

        public string Provider => "TestArbiter";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = JsonDocument.Parse(_rawJson).RootElement.Clone(), Model = request.Model });
    }

    /// <summary>Throws from the brain call — models a transient API / transport error (429, 500, no-tool-block) or, with a cancellation, the torn-down run.</summary>
    private sealed class FakeThrowingArbiterClient : ILLMClient, IStructuredLLMClient
    {
        private readonly Exception _toThrow;
        public FakeThrowingArbiterClient(Exception? toThrow = null) => _toThrow = toThrow ?? new InvalidOperationException("brain unreachable");

        public string Provider => "TestArbiter";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            throw _toThrow;
    }

    private sealed class FakeSelector : IModelPoolSelector
    {
        private readonly ModelPoolPick? _pick;
        private FakeSelector(ModelPoolPick? pick) => _pick = pick;

        public static FakeSelector WithModel(string modelId = "claude-sonnet-4-6") =>
            new(new ModelPoolPick { ModelId = modelId, Credential = new ResolvedModelCredential { Provider = "TestArbiter", ApiKey = "sk-test" } });

        public static FakeSelector Empty() => new(null);

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult(_pick);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult(_pick);

        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
    }
}
