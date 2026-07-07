using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
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
    public void Project_gate_carries_score_issues_evidence_and_derives_approval_from_severity()
    {
        var json = Parse("""{ "approved": false, "score": 55, "issues": [{ "issue": "no rollback plan", "evidence": "the plan has no rollback subtask", "severity": "blocker" }], "rationale": "thin" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Gate, json);

        verdict.Failed.ShouldBeFalse();
        verdict.Mode.ShouldBe(ReviewMode.Gate);
        verdict.Approved.ShouldBeFalse("a Blocker halts the gate");
        verdict.Score.ShouldBe(55);
        verdict.Issues.ShouldContain(i => i.Text == "no rollback plan" && i.Evidence == "the plan has no rollback subtask" && i.Severity == CriticSeverity.Blocker, "issues carry their evidence (S8) AND severity (P1)");
        verdict.Rationale.ShouldBe("thin");
    }

    [Theory]
    // The model's raw `approved` bit is ADVISORY; SEVERITY is authoritative. A Blocker halts even if the model
    // said approved:true (under-call safety); a Major/Minor-only flag no longer halts even if the model said
    // approved:false (the calibration fix — "correctly addresses the goal but has a material flaw" proceeds).
    [InlineData("blocker", true, false)]    // a Blocker halts regardless of the raw approved bit
    [InlineData("blocker", false, false)]
    [InlineData("major", false, true)]      // a Major-only disapproval no longer halts — the calibration fix
    [InlineData("minor", false, true)]      // a nitpick never halts
    [InlineData("bogus", false, true)]      // an unknown severity degrades to Major → does not halt
    public void Gate_approval_is_severity_authoritative(string severity, bool rawApproved, bool expectedApproved)
    {
        var json = Parse($$"""{ "approved": {{(rawApproved ? "true" : "false")}}, "issues": [{ "issue": "x", "evidence": "y", "severity": "{{severity}}" }], "rationale": "r" }""");

        LlmStructuredCritic.Project(ReviewMode.Gate, json).Approved.ShouldBe(expectedApproved);
    }

    [Fact]
    public void Project_gate_with_no_issues_approves()
    {
        var json = Parse("""{ "approved": true, "issues": [], "rationale": "clean" }""");

        LlmStructuredCritic.Project(ReviewMode.Gate, json).Approved.ShouldBeTrue("no issue ⇒ no blocker ⇒ approved");
    }

    [Fact]
    public void Project_improve_carries_the_critique_when_a_material_issue_warrants_it()
    {
        var json = Parse("""{ "critique": "add an integration test subtask", "issues": [{ "issue": "untested path", "evidence": "subtask s2 names no test", "severity": "major" }], "rationale": "missing coverage" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Improve, json);

        verdict.Failed.ShouldBeFalse();
        verdict.Mode.ShouldBe(ReviewMode.Improve);
        verdict.Critique.ShouldBe("add an integration test subtask", "a Major issue warrants the revision");
        verdict.Issues.ShouldContain(i => i.Text == "untested path" && i.Severity == CriticSeverity.Major);
    }

    [Fact]
    public void The_plan_review_prompt_adds_the_acceptance_satisfiability_check_only_for_plans()
    {
        // ⑧ (fallback rung): when the grounded reviewer ladders DOWN to the model critic (ArtifactKind "workflow plan"),
        // the critic asks whether each subtask's acceptance can be verified as written — the endless-retry error class.
        var planPrompt = LlmStructuredCritic.BuildUserPromptForTest(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "workflow plan", Artifact = "plan text", Goal = "ship" });

        planPrompt.ShouldContain("ACCEPTANCE SATISFIABILITY", customMessage: "⑧: a PLAN review asks whether each subtask's acceptance can ever pass as written");
        planPrompt.ShouldContain("endless retry", customMessage: "the error class the satisfiability check guards against");

        // The clause is PLAN-SCOPED — the generic critic is byte-identical for every other artifact kind.
        var changePrompt = LlmStructuredCritic.BuildUserPromptForTest(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = "a diff", Goal = "ship" });

        changePrompt.ShouldNotContain("ACCEPTANCE SATISFIABILITY", customMessage: "the clause is plan-scoped — a non-plan artifact review is unchanged (the generic critic is not widened for every kind)");
    }

    [Fact]
    public void Project_improve_suppresses_a_minor_only_critique()
    {
        // A revision round is not worth spending on nitpicks — the critique is suppressed so the producer keeps its
        // output, while the verdict (and its minor issues) still surfaces for the journal.
        var json = Parse("""{ "critique": "rename the variable for clarity", "issues": [{ "issue": "terse name", "evidence": "`x` at line 3", "severity": "minor" }], "rationale": "cosmetic only" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Improve, json);

        verdict.Failed.ShouldBeFalse("the review ran — it is not a failed review");
        verdict.Critique.ShouldBeNull("a minor-only critique does not warrant a revision round → suppressed");
        verdict.Issues.ShouldHaveSingleItem().Severity.ShouldBe(CriticSeverity.Minor);
    }

    [Fact]
    public void Project_improve_keeps_a_free_text_critique_with_no_structured_issues()
    {
        var json = Parse("""{ "critique": "the whole approach is off", "issues": [], "rationale": "reconsider" }""");

        LlmStructuredCritic.Project(ReviewMode.Improve, json).Critique
            .ShouldBe("the whole approach is off", "a critique with no structured issues keeps its revision — unknown severity must not be silently dropped");
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
