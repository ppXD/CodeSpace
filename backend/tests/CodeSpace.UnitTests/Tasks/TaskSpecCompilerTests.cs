using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Agents;
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
        suggestion!.AcceptanceChecks.ShouldBe(new[] { "dotnet", "test" }, "argv tokens are trimmed");
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
            new[] { "acceptanceChecks", "acceptanceCriteria", "hasDeliveryOpinion", "openPullRequest", "confidence", "rationale" },
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
        result.Suggestion!.AcceptanceChecks.ShouldBe(new[] { "dotnet", "test" });
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

    // ── Fakes at the honest seams ───────────────────────────────────────────────────

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

    private sealed class NullGrounding : IRepoGroundingProvider
    {
        public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FixedGrounding : IRepoGroundingProvider
    {
        private readonly string _text;
        public FixedGrounding(string text) => _text = text;
        public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken) => Task.FromResult<string?>(_text);
    }

    private sealed class ThrowingGrounding : IRepoGroundingProvider
    {
        public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken) => throw new InvalidOperationException("grounding down");
    }
}
