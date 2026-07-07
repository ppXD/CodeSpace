using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The critic's projection (fail-closed per mode) + the schema/toggle pins — without a real model. GATE projects
/// approved/score/issues; IMPROVE projects a critique and fails closed on a blank one; the kill-switch defaults ON and
/// off only for an explicit disable.
/// </summary>
[Trait("Category", "Unit")]
public class LlmStructuredCriticTests
{
    [Fact]
    public void Project_gate_carries_approved_score_and_issues()
    {
        var json = Parse("""{ "approved": false, "score": 55, "issues": [{ "issue": "no rollback plan", "evidence": "the plan has no rollback subtask" }], "rationale": "thin" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Gate, json);

        verdict.Failed.ShouldBeFalse();
        verdict.Mode.ShouldBe(ReviewMode.Gate);
        verdict.Approved.ShouldBeFalse();
        verdict.Score.ShouldBe(55);
        verdict.Issues.ShouldContain(i => i.Text == "no rollback plan" && i.Evidence == "the plan has no rollback subtask", "issues carry their evidence (S8)");
        verdict.Rationale.ShouldBe("thin");
    }

    [Fact]
    public void Project_improve_carries_the_critique()
    {
        var json = Parse("""{ "critique": "add an integration test subtask", "issues": [{ "issue": "untested path", "evidence": "subtask s2 names no test" }], "rationale": "missing coverage" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Improve, json);

        verdict.Failed.ShouldBeFalse();
        verdict.Mode.ShouldBe(ReviewMode.Improve);
        verdict.Critique.ShouldBe("add an integration test subtask");
        verdict.Issues.ShouldContain(i => i.Text == "untested path", "improve issues project the same evidence-attached shape");
    }

    [Fact]
    public void Project_improve_fails_closed_on_a_blank_critique()
    {
        var json = Parse("""{ "critique": "", "rationale": "ok" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Improve, json);

        verdict.Failed.ShouldBeTrue("a blank critique is nothing to revise against → a failed review (the caller keeps the original)");
    }

    [Fact]
    public void Project_gate_degrades_a_blank_rationale_to_a_placeholder()
    {
        var verdict = LlmStructuredCritic.Project(ReviewMode.Gate, Parse("""{ "approved": true, "rationale": "" }"""));

        verdict.Approved.ShouldBeTrue();
        verdict.Rationale.ShouldNotBeNullOrWhiteSpace("a blank rationale degrades to a placeholder, never silent");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("garbage", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("  False  ", false)]
    public void CriticToggle_defaults_on_and_is_off_only_for_an_explicit_disable(string? raw, bool expected)
        => CriticToggle.IsEnabled(raw).ShouldBe(expected);

    [Fact]
    public void The_two_schemas_are_well_formed_objects()
    {
        CriticSchema.GateSchema.GetProperty("properties").TryGetProperty("approved", out _).ShouldBeTrue();
        CriticSchema.ImproveSchema.GetProperty("properties").TryGetProperty("critique", out _).ShouldBeTrue();
        CriticSchema.GateSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        CriticSchema.ImproveSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Review_relabels_the_ambient_recording_scope_as_critic_review()
    {
        // K/L2: the critic's model call must record under "critic.review" — not its CALLER's kind ("supervisor.decision",
        // a planner node's type key) — so the journal says what the call was doing. One nesting inside the critic covers
        // every caller; the ambient kind is restored after the review (scoped, not a leak).
        var captured = new List<string?>();
        var critic = new LlmStructuredCritic(new SingleClientRegistry(new KindCapturingClient(captured)), new PickByRowSelector());

        var scope = new Core.Services.Workflows.Llm.LlmCallScope(Guid.NewGuid(), Guid.NewGuid(), "sup", "sup#turn0", "supervisor.decision", Logger: null!, Offloader: null!);

        using (Core.Services.Workflows.Llm.LlmCallContext.Push(scope))
        {
            var verdict = await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "plan", Artifact = "a", Goal = "g" }, Guid.NewGuid(), reviewerModelId: Guid.NewGuid(), CancellationToken.None);

            verdict.Failed.ShouldBeFalse("the happy path reached the model");
            Core.Services.Workflows.Llm.LlmCallContext.Current!.Kind.ShouldBe("supervisor.decision", "the re-label is scoped to the review call");
        }

        string.Join("|", captured).ShouldBe(LlmStructuredCritic.ReviewCallKind, "the model call saw the critic's own kind");
        LlmStructuredCritic.ReviewCallKind.ShouldBe("critic.review");
    }

    private sealed class KindCapturingClient : Core.Services.Workflows.Llm.ILLMClient, Core.Services.Workflows.Llm.IStructuredLLMClient
    {
        private readonly List<string?> _captured;

        public KindCapturingClient(List<string?> captured) { _captured = captured; }

        public string Provider => "TestCritic";

        public Task<Core.Services.Workflows.Llm.LLMCompletion> CompleteAsync(Core.Services.Workflows.Llm.LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Core.Services.Workflows.Llm.StructuredLLMCompletion> CompleteStructuredAsync(Core.Services.Workflows.Llm.StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            _captured.Add(Core.Services.Workflows.Llm.LlmCallContext.Current?.Kind);
            return Task.FromResult(new Core.Services.Workflows.Llm.StructuredLLMCompletion { Json = Parse("""{ "approved": true, "rationale": "ok" }"""), Model = "m" });
        }
    }

    private sealed class SingleClientRegistry : Core.Services.Workflows.Llm.ILLMClientRegistry
    {
        public SingleClientRegistry(Core.Services.Workflows.Llm.ILLMClient client) => All = new[] { client };
        public IReadOnlyList<Core.Services.Workflows.Llm.ILLMClient> All { get; }
        public Core.Services.Workflows.Llm.ILLMClient Resolve(string provider) => All[0];
    }

    private sealed class PickByRowSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = "m", Credential = new Messages.Agents.ResolvedModelCredential { Provider = "TestCritic", ApiKey = "sk" } });

        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── SelectReviewerRowIdAsync default member (S4d): fakes without an override inherit today's pick ──

    [Fact]
    public async Task A_selector_without_the_reviewer_override_delegates_to_the_brain_pick()
    {
        // Every test fake implementing IModelPoolSelector compiles UNCHANGED because the reviewer pick is a
        // DEFAULT interface member — and behaviorally it must be today's brain pick (same-model allowed), so
        // the ladder is an opt-in override of the real selector, never a silent behavior change for fakes.
        var brainRow = Guid.NewGuid();
        IModelPoolSelector selector = new BrainOnlySelector(brainRow);

        (await selector.SelectReviewerRowIdAsync(Guid.NewGuid(), new[] { "Anthropic" }, producerRowId: brainRow, CancellationToken.None))
            .ShouldBe(brainRow, "the default member delegates to SelectBrainRowIdAsync — the producer preference is the REAL selector's override");
    }

    private sealed class BrainOnlySelector : IModelPoolSelector
    {
        private readonly Guid _brainRow;

        public BrainOnlySelector(Guid brainRow) { _brainRow = brainRow; }

        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(_brainRow);

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
