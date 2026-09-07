using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// 🟢 Unit: pins P5-7 (I1 spec compiler, first slice) — the pure reply→suggestion mapping, the schema commit-
/// contract, and the 兜底 posture (any model-path miss → null suggestion, never a throw). The AUTHORITY property
/// is structural and pinned by absence: the compiler returns plain suggestions, persists nothing, stakes nothing —
/// the operator's own launch submit is the only path to the ledger (P5-4's Operator provenance carrier).
/// </summary>
[Trait("Category", "Unit")]
public class TaskSpecCompilerTests
{
    [Theory]
    [InlineData("The tests for the payment retry path are flaky; make them deterministic.")]
    [InlineData("Do not run `go test ./... -count=5`; investigate the report without assuming a Go toolchain.")]
    public async Task A_model_command_without_independent_source_assessment_never_becomes_a_mandatory_check(string goal)
    {
        var client = new RecordingStructuredClient("""{"acceptanceChecks":["sh","-c","go test ./... -count=5"],"acceptanceCriteria":["Retry behavior is deterministic"],"hasDeliveryOpinion":false,"openPullRequest":false,"confidence":0.9,"rationale":"No repository evidence, so no command is suggested."}""");
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);
        var result = await compiler.CompileAsync(Guid.NewGuid(), goal, null, CancellationToken.None);
        result.Suggestion.ShouldNotBeNull();
        result.Suggestion.AcceptanceChecks.ShouldBeEmpty("a generated argv and a confident rationale do not establish its source, dependencies or semantic support");
        result.Suggestion.AcceptanceCriteria.ShouldBe(["Retry behavior is deterministic"]);
    }

    // ── ToSuggestion: the pure mapping ──────────────────────────────────────────────

    [Fact]
    public void A_full_reply_maps_onto_the_launch_surface_fields()
    {
        var suggestion = TaskSpecCompiler.ToSuggestion(new TaskSpecCompilation
        {
            AcceptanceChecks = new[] { " dotnet ", "test" },
            AcceptanceCriteria = new[] { " builds green ", "builds green", "no new warnings" },
            HasDeliveryOpinion = true, OpenPullRequest = true, TargetBranch = " release/2.0 ",
            Confidence = 0.8, Rationale = "test project exists",
        });

        suggestion.ShouldNotBeNull();
        suggestion!.AcceptanceChecks.ShouldBeEmpty("unassessed model output is never a mandatory floor");
        suggestion.AcceptanceProposal!.Argv.ShouldBe(new[] { " dotnet ", "test" }, "argv tokens preserve their exact semantics rather than being trimmed");
        suggestion.AcceptanceProposal.Status.ShouldBe(TaskSpecEvidenceStatus.Unknown);
        suggestion.AcceptanceCriteria.ShouldBe(new[] { "builds green", "no new warnings" }, "criteria are trimmed + deduped");
        suggestion.OpenPullRequest.ShouldBe(true);
        suggestion.TargetBranch.ShouldBe("release/2.0");
        suggestion.Confidence.ShouldBe(0.8);
        suggestion.Rationale.ShouldBe("test project exists");
    }

    [Fact]
    public void No_delivery_opinion_never_invents_one()
    {
        // The model MUST claim hasDeliveryOpinion for openPullRequest/targetBranch to count — "no opinion" is the
        // common case and an invented preference would ride the launch as if the operator chose it.
        var suggestion = TaskSpecCompiler.ToSuggestion(new TaskSpecCompilation
        {
            AcceptanceChecks = new[] { "dotnet", "test" },
            HasDeliveryOpinion = false, OpenPullRequest = true, TargetBranch = "main",
            Confidence = 0.9,
        });

        suggestion!.OpenPullRequest.ShouldBeNull("openPullRequest is ignored without a claimed opinion");
        suggestion.TargetBranch.ShouldBeNull();
    }

    [Fact]
    public void An_empty_reply_maps_to_null_not_an_empty_scaffold()
    {
        TaskSpecCompiler.ToSuggestion(new TaskSpecCompilation { AcceptanceChecks = new[] { "  " }, Confidence = 0.5 })
            .ShouldBeNull("whitespace-only checks filter to nothing; nothing suggested → nothing rendered");
    }

    [Theory]
    [InlineData(1.7, 1.0)]
    [InlineData(-0.3, 0.0)]
    public void Confidence_is_clamped(double raw, double clamped)
    {
        TaskSpecCompiler.ToSuggestion(new TaskSpecCompilation { AcceptanceCriteria = new[] { "done" }, Confidence = raw })!
            .Confidence.ShouldBe(clamped);
    }

    [Fact]
    public void A_blank_rationale_gets_the_default_line()
    {
        TaskSpecCompiler.ToSuggestion(new TaskSpecCompilation { AcceptanceCriteria = new[] { "done" }, Rationale = "  " })!
            .Rationale.ShouldBe("Compiled from the goal.");
    }

    // ── The schema commit-contract ──────────────────────────────────────────────────

    [Fact]
    public void The_response_schema_is_pinned()
    {
        var schema = TaskSpecCompilerSchema.ResponseSchema;

        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(
            new[] { "acceptanceChecks", "evidencePaths", "dependencies", "acceptanceCriteria", "hasDeliveryOpinion", "openPullRequest", "confidence", "rationale" },
            "the commit-contract: a drift here is a reviewer-visible contract change");
        schema.GetProperty("properties").GetProperty("targetBranch").GetProperty("description").GetString()!
            .ShouldContain("never guess a branch");
        schema.GetProperty("properties").GetProperty("acceptanceChecks").GetProperty("description").GetString()!
            .ShouldContain("EMPTY when unsure", customMessage: "the wrong-check-is-worse-than-none rule is taught in the contract itself");
    }

    // ── The 兜底: any model-path miss → null suggestion, never a throw ───────────────

    [Fact]
    public async Task No_structured_provider_degrades_to_a_null_suggestion()
    {
        var compiler = new TaskSpecCompiler(new EmptyRegistry(), new NoPoolSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);

        var result = await compiler.CompileAsync(Guid.NewGuid(), "fix the bug", repositoryId: null, CancellationToken.None);

        result.Suggestion.ShouldBeNull("no model → nothing suggested, never a throw (the launch composer must never break)");
        result.Grounded.ShouldBeFalse();
    }

    [Fact]
    public async Task The_goal_and_grounding_reach_the_model_and_the_reply_reaches_the_caller()
    {
        var client = new RecordingStructuredClient("""{"acceptanceChecks":["dotnet","test"],"acceptanceCriteria":["tests pass"],"hasDeliveryOpinion":true,"openPullRequest":true,"targetBranch":"","confidence":0.7,"rationale":"repo has a test project"}""");
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new FixedGrounding("Repository top-level layout: src/, tests/"), NullLogger<TaskSpecCompiler>.Instance);

        var result = await compiler.CompileAsync(Guid.NewGuid(), "fix the parser bug", Guid.NewGuid(), CancellationToken.None);

        result.Grounded.ShouldBeTrue();
        result.Suggestion.ShouldNotBeNull();
        result.Suggestion!.AcceptanceChecks.ShouldBeEmpty("layout names plus a generation reply cannot establish semantic source support");
        result.Suggestion.AcceptanceProposal!.Argv.ShouldBe(new[] { "dotnet", "test" });
        result.Suggestion.OpenPullRequest.ShouldBe(true);
        result.Suggestion.TargetBranch.ShouldBeNull("an empty targetBranch means the repo default — never an empty string on the wire");

        client.LastRequest!.UserPrompt.ShouldContain("fix the parser bug");
        client.LastRequest.UserPrompt.ShouldContain("Repository top-level layout", Case.Sensitive, "grounding is folded as ground truth for the toolchain claim");
        client.LastRequest.Temperature.ShouldBe(0.0);
    }

    [Fact]
    public async Task A_grounding_fault_degrades_to_an_ungrounded_compile_never_a_failed_preview()
    {
        var client = new RecordingStructuredClient("""{"acceptanceChecks":[],"acceptanceCriteria":["done"],"hasDeliveryOpinion":false,"openPullRequest":false,"confidence":0.4,"rationale":"r"}""");
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new ThrowingGrounding(), NullLogger<TaskSpecCompiler>.Instance);

        var result = await compiler.CompileAsync(Guid.NewGuid(), "write the report", Guid.NewGuid(), CancellationToken.None);

        result.Grounded.ShouldBeFalse();
        result.Suggestion.ShouldNotBeNull("the compile proceeds ungrounded");
        client.LastRequest!.UserPrompt.ShouldNotContain("Repository top-level layout");
    }

    [Fact]
    public async Task A_client_fault_degrades_to_a_null_suggestion()
    {
        var compiler = new TaskSpecCompiler(new SingleRegistry(new ThrowingStructuredClient()), new OnePickSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);

        (await compiler.CompileAsync(Guid.NewGuid(), "fix it", null, CancellationToken.None)).Suggestion.ShouldBeNull();
    }

    [Fact]
    public async Task It_asks_the_pool_for_a_ceilinged_model_never_the_teams_strongest()
    {
        // The spec preview is a SUGGESTION the operator edits before anything is staked — one of D2's four cheap
        // callers, so it must not automatically spend the team's Frontier model.
        var client = new RecordingStructuredClient("""{"acceptanceChecks":[],"acceptanceCriteria":["done"],"hasDeliveryOpinion":false,"openPullRequest":false,"confidence":0.4,"rationale":"r"}""");
        var selector = new OnePickSelector();

        await new TaskSpecCompiler(new SingleRegistry(client), selector, new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance)
            .CompileAsync(Guid.NewGuid(), "fix it", null, CancellationToken.None);

        selector.SeenCeiling.ShouldBe(InProcessStructuredModel.CheapBrainCeiling, "the spec-preview compiler is one of D2's four cheap callers");
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("timed-out")]
    [InlineData("malformed")]
    public async Task A_failed_semantic_review_keeps_the_proposal_and_criteria_with_honest_call_accounting(string reviewOutcome)
    {
        var client = new SequenceStructuredClient(reviewOutcome);
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);
        var result = await compiler.CompileAsync(Guid.NewGuid(), SequenceStructuredClient.Goal, null, CancellationToken.None);
        result.Suggestion.ShouldNotBeNull();
        result.Suggestion.AcceptanceChecks.ShouldBeEmpty();
        result.Suggestion.AcceptanceProposal!.Argv.ShouldBe(["custom-audit", "--check"]);
        result.Suggestion.AcceptanceProposal.Status.ShouldBe(TaskSpecEvidenceStatus.Unknown);
        result.Suggestion.AcceptanceCriteria.ShouldBe(["The report meets the requested requirements."]);
        result.ModelCalls!.Count.ShouldBe(2);
        result.ModelCalls[0].Outcome.ShouldBe("succeeded");
        result.ModelCalls[0].InputTokens.ShouldBe(10);
        result.ModelCalls[1].Phase.ShouldBe("semantic-review");
        result.ModelCalls[1].Outcome.ShouldBe(reviewOutcome);
        if (reviewOutcome != "malformed")
        {
            result.ModelCalls[1].ActualModel.ShouldBeNull();
            result.ModelCalls[1].InputTokens.ShouldBeNull("unknown provider usage must not turn into zero cost");
            result.ModelCalls[1].UsageMayBeIncomplete.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Independent_review_sees_original_sources_without_the_proposers_rationale_and_records_the_actual_failover_model()
    {
        var client = new SequenceStructuredClient("failover-success");
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);
        var result = await compiler.CompileAsync(Guid.NewGuid(), SequenceStructuredClient.Goal, null, CancellationToken.None);
        result.Suggestion!.AcceptanceChecks.ShouldBe(["custom-audit", "--check"]);
        result.Suggestion.AcceptanceProposal!.Source.ShouldBe(TaskSpecCheckSource.UserExplicit);
        client.Requests.Count.ShouldBe(2);
        client.Requests[1].UserPrompt.ShouldContain(SequenceStructuredClient.Goal);
        client.Requests[1].UserPrompt.ShouldNotContain("PROPOSER_SELF_ENDORSEMENT");
        client.Requests[1].JsonSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).ShouldContain("citations");
        var trace = result.ModelCalls![1];
        trace.SelectedModel.ShouldBe("test-model");
        trace.ActualModel.ShouldBe("fallback-model");
        trace.FailedOver.ShouldBe(["TestSpec:test-model — transient 503"]);
        trace.InputTokens.ShouldBe(20);
        trace.OutputTokens.ShouldBe(7);
        trace.UsageMayBeIncomplete.ShouldBeTrue("the failed-over attempt's billed usage was not reported");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_turning_into_a_successful_unknown_preview()
    {
        var client = new SequenceStructuredClient("failover-success");
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => compiler.CompileAsync(Guid.NewGuid(), SequenceStructuredClient.Goal, null, cancelled.Token));
        client.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(20, 7, true, true)]
    [InlineData(-1, 7, false, true)]
    [InlineData(20, -1, false, true)]
    [InlineData(0, 0, false, false)]
    public async Task Preview_trace_preserves_usage_completeness_even_without_a_failover(int input, int output, bool partial, bool incomplete)
    {
        var client = new SequenceStructuredClient("success") { ReviewUsage = new LlmUsage { InputTokens = input, OutputTokens = output, IsPartial = partial }, ReviewFailedOver = [] };
        var compiler = new TaskSpecCompiler(new SingleRegistry(client), new OnePickSelector(), new NullGrounding(), NullLogger<TaskSpecCompiler>.Instance);
        var result = await compiler.CompileAsync(Guid.NewGuid(), SequenceStructuredClient.Goal, null, CancellationToken.None);
        var trace = result.ModelCalls![1];
        trace.Outcome.ShouldBe("succeeded");
        trace.FailedOver.ShouldBeEmpty();
        trace.InputTokens.ShouldBe(input);
        trace.OutputTokens.ShouldBe(output);
        trace.UsageMayBeIncomplete.ShouldBe(incomplete);
    }

    // ── Fakes at the honest seams ───────────────────────────────────────────────────

    private sealed class SequenceStructuredClient : ILLMClient, IStructuredLLMClient
    {
        public const string Goal = "Use custom-audit --check for final validation.";
        private readonly string _reviewOutcome;
        public List<StructuredLLMCompletionRequest> Requests { get; } = [];
        public SequenceStructuredClient(string reviewOutcome) { _reviewOutcome = reviewOutcome; }
        public LlmUsage ReviewUsage { get; init; } = new() { InputTokens = 20, OutputTokens = 7 };
        public IReadOnlyList<string> ReviewFailedOver { get; init; } = ["TestSpec:test-model — transient 503"];
        public string Provider => "TestSpec";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1) return Task.FromResult(new StructuredLLMCompletion
            {
                Model = "proposal-model", Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 },
                Json = JsonDocument.Parse("""{"acceptanceChecks":["custom-audit","--check"],"evidencePaths":[],"dependencies":[{"requirement":"Validator input exists","validationStrategy":"Inspect input and invoke the requested validator"}],"acceptanceCriteria":["The report meets the requested requirements."],"hasDeliveryOpinion":false,"openPullRequest":false,"confidence":0.8,"rationale":"PROPOSER_SELF_ENDORSEMENT"}""").RootElement.Clone(),
            });
            if (_reviewOutcome == "failed") throw new IOException("review transport unavailable");
            if (_reviewOutcome == "timed-out") throw new OperationCanceledException("review request deadline");
            var json = _reviewOutcome == "malformed" ? "[]" : JsonSerializer.Serialize(new TaskSpecReview { Source = "user-explicit", Support = "supported", Citations = [new TaskSpecReviewCitation("goal", Goal)], Reason = "The original user explicitly requested this command; it has not been executed." });
            return Task.FromResult(new StructuredLLMCompletion { Model = "fallback-model", Json = JsonDocument.Parse(json).RootElement.Clone(), Usage = ReviewUsage, FailedOver = ReviewFailedOver });
        }
    }

    private sealed class EmptyRegistry : ILLMClientRegistry
    {
        public IReadOnlyList<ILLMClient> All => Array.Empty<ILLMClient>();
        public ILLMClient Resolve(string provider) => throw new NotSupportedException();
    }

    private sealed class SingleRegistry : ILLMClientRegistry
    {
        public SingleRegistry(ILLMClient client) => All = new[] { client };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All[0];
    }

    private sealed class RecordingStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly string _replyJson;
        public StructuredLLMCompletionRequest? LastRequest;

        public RecordingStructuredClient(string replyJson) => _replyJson = replyJson;

        public string Provider => "TestSpec";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new StructuredLLMCompletion { Json = JsonDocument.Parse(_replyJson).RootElement.Clone(), Model = request.Model });
        }
    }

    private sealed class ThrowingStructuredClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "TestSpec";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class OnePickSelector : IModelPoolSelector
    {
        private static readonly ModelPoolPick Pick = new() { ModelId = "test-model", Credential = new ResolvedModelCredential { Provider = "TestSpec", ApiKey = "sk-test" } };

        /// <summary>The ceiling the compiler asked for (D2) — null until it resolves a model, so a test can prove the ARGUMENT, not merely the outcome.</summary>
        public ModelCapabilityTier? SeenCeiling { get; private set; }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, ModelCapabilityTier? tierCeiling, CancellationToken cancellationToken)
        {
            SeenCeiling = tierCeiling;
            return SelectAsync(teamId, provider, allowedModels, pinnedModel, cancellationToken);
        }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(Pick);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(Pick);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoPoolSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NullGrounding : ITaskSpecEvidenceReader
    {
        public Task<TaskSpecEvidenceContext> CaptureAsync(TaskSpecEvidenceRequest request, CancellationToken cancellationToken) => Task.FromResult(TaskSpecEvidenceContext.TaskOnly(request, TaskSpecRepositoryState.NotRequested, "No repository."));
        public Task<TaskSpecEvidenceContext> ReadFilesAsync(TaskSpecEvidenceContext context, IReadOnlyList<string> paths, CancellationToken cancellationToken) => Task.FromResult(context);
    }

    private sealed class FixedGrounding : ITaskSpecEvidenceReader
    {
        private readonly string _text;
        public FixedGrounding(string text) => _text = text;
        public Task<TaskSpecEvidenceContext> CaptureAsync(TaskSpecEvidenceRequest request, CancellationToken cancellationToken) => Task.FromResult(new TaskSpecEvidenceContext(request, new TaskSpecRepositoryObservation { State = TaskSpecRepositoryState.Observed, Reference = "abc123", Detail = "Root observed" }, [TaskSpecSource.Create("goal", "user-goal", request.Goal), TaskSpecSource.Create("repository-layout", "repository-layout", _text, reference: "abc123")]));
        public Task<TaskSpecEvidenceContext> ReadFilesAsync(TaskSpecEvidenceContext context, IReadOnlyList<string> paths, CancellationToken cancellationToken) => Task.FromResult(context);
    }

    private sealed class ThrowingGrounding : ITaskSpecEvidenceReader
    {
        public Task<TaskSpecEvidenceContext> CaptureAsync(TaskSpecEvidenceRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("grounding down");
        public Task<TaskSpecEvidenceContext> ReadFilesAsync(TaskSpecEvidenceContext context, IReadOnlyList<string> paths, CancellationToken cancellationToken) => throw new InvalidOperationException("grounding down");
    }
}
