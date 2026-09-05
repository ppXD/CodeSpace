using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the deterministic baseline heuristic classifier: the transparent keyword → generic-signal derivation,
/// the suggested-recipe (the only shipped recipe), and — the load-bearing HONESTY INVARIANT — that its
/// confidence is ALWAYS strictly below <see cref="EffortPolicy.ConfirmConfidenceFloor"/> for ANY goal, so the
/// auto path ALWAYS asks the operator to confirm. The heuristic guesses; it never silently decides.
/// </summary>
[Trait("Category", "Unit")]
public class HeuristicEffortClassifierTests
{
    private static readonly HeuristicEffortClassifier Classifier = new();

    private static async Task<EffortDecision> ClassifyAsync(string goal) =>
        await Classifier.ClassifyAsync(new EffortRouteRequest { Seed = Seed(goal) }, CancellationToken.None);

    private static TaskLaunchSeed Seed(string goal) => new() { Goal = goal, SurfaceKind = "test", TeamId = Guid.NewGuid() };

    [Theory]
    [InlineData("Fix the null check in the parser", true)]      // "fix" verb
    [InlineData("Implement the new endpoint", true)]            // "implement" verb
    [InlineData("What does this function return?", false)]      // a question, no code-change verb
    public async Task Derives_needs_code_change_from_verbs(string goal, bool expected)
    {
        (await ClassifyAsync(goal)).Signals.NeedsCodeChange.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Refactor the logger across all modules", true)]   // "across" + "all"
    [InlineData("Rename a single private field", false)]
    public async Task Derives_cross_file_from_scope_words(string goal, bool expected)
    {
        (await ClassifyAsync(goal)).Signals.CrossFile.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Add a unit test for the matcher", true)]   // "test"
    [InlineData("Make CI green again", true)]               // "ci"
    [InlineData("Update the README wording", false)]
    public async Task Derives_needs_tests_or_ci(string goal, bool expected)
    {
        (await ClassifyAsync(goal)).Signals.NeedsTestsOrCi.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Drop the legacy column and migrate prod data", true)]   // "drop" + "migrate" + "prod"
    [InlineData("Deploy the build to production", true)]                 // "deploy" + "production"
    [InlineData("Rotate the API secret", true)]                          // "secret" + "rotate"
    [InlineData("Add a tooltip to the button", false)]
    public async Task Derives_risky_side_effects(string goal, bool expected)
    {
        (await ClassifyAsync(goal)).Signals.RiskySideEffects.ShouldBe(expected);
    }

    [Fact]
    public async Task Suggests_the_only_shipped_recipe_and_stamps_its_kind()
    {
        var decision = await ClassifyAsync("Fix a bug in the auth flow");

        decision.SuggestedRecipe.ShouldBe(TaskRecipeKinds.SingleAgent);
        decision.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind);
        decision.Rationale.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Suggested_effort_matches_the_policy_over_the_derived_signals()
    {
        // A risky goal classifies to deep via the policy's first row.
        (await ClassifyAsync("Delete the old table and migrate production data across all services")).SuggestedEffort.ShouldBe(TaskEffortModes.Deep);

        // A localized code-only goal falls to the quick catch-all.
        (await ClassifyAsync("Fix a typo")).SuggestedEffort.ShouldBe(TaskEffortModes.Quick);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("Fix a typo")]
    [InlineData("Refactor the entire authentication subsystem across every module, add comprehensive unit and integration test coverage, migrate the production database schema, rotate all secrets, and deploy to production with a full rollback plan documented in detail for the operator")]
    public async Task Confidence_is_always_strictly_below_the_confirm_floor(string goal)
    {
        var decision = await ClassifyAsync(goal);

        decision.Confidence.ShouldBeLessThan(EffortPolicy.ConfirmConfidenceFloor,
            customMessage: "the heuristic must ALWAYS stay below the confirm floor so the auto path always asks the operator — it guesses, it never silently decides");
        decision.Confidence.ShouldBeLessThanOrEqualTo(HeuristicEffortClassifier.ConfidenceCap);
    }

    [Theory]
    // Document — an explicit written artefact is named.
    [InlineData("Write a design doc for the new scheduler", DeliverableShapes.Document)]
    [InlineData("Draft an RFC for the storage rewrite", DeliverableShapes.Document)]
    // Research — an investigation verb, ahead of the question words it often co-occurs with.
    [InlineData("Investigate why the nightly job stalls", DeliverableShapes.Research)]
    [InlineData("Compare the two retry strategies", DeliverableShapes.Research)]
    // Answer — a question / explanation, no artefact named.
    [InlineData("Explain how the retry loop works", DeliverableShapes.Answer)]
    [InlineData("What does this function return?", DeliverableShapes.Answer)]
    // Code — the conservative fall-through: anything unrecognised keeps today's coding projection.
    [InlineData("Fix the failing login test", DeliverableShapes.Code)]
    [InlineData("Add a tooltip to the button", DeliverableShapes.Code)]
    [InlineData("", DeliverableShapes.Code)]
    public async Task Infers_a_coarse_deliverable_shape_from_verbs(string goal, string expected)
    {
        (await ClassifyAsync(goal)).Signals.DeliverableShape.ShouldBe(expected);
    }

    [Theory]
    // The refutation: an answer word inside a CODING request used to win outright, so a bug report phrased as a
    // question projected as read-only research with a judged DELIVERABLE.md and never touched the bug.
    [InlineData("Fix the login 500 — why does it hang?")]
    [InlineData("Explain the retry loop and then add a backoff")]
    [InlineData("Investigate the flaky test and fix it")]
    [InlineData("What is wrong with the parser? Update it.")]
    public async Task A_goal_that_asks_for_a_code_change_stays_code_shaped_however_it_is_phrased(string goal)
    {
        var signals = (await ClassifyAsync(goal)).Signals;

        signals.NeedsCodeChange.ShouldBeTrue("the code-change signal is what makes this a coding request, whatever question rides along");
        signals.DeliverableShape.ShouldBe(DeliverableShapes.Code,
            customMessage: "a task that must change code is CODE-shaped — the answer / research shapes disarm the coding projection and grade a report instead");
    }

    [Fact]
    public async Task An_explicit_written_artefact_outranks_the_code_change_signal()
    {
        // "Write a design doc" trips the code-change verb "write" — the document shape must still win, or the one
        // phrasing that unambiguously names a written deliverable would be the one that never gets it.
        var signals = (await ClassifyAsync("Write a design doc for the new scheduler")).Signals;

        signals.NeedsCodeChange.ShouldBeTrue("'write' is a code-change verb — that is exactly what makes this case load-bearing");
        signals.DeliverableShape.ShouldBe(DeliverableShapes.Document);
    }

    [Fact]
    public async Task The_inferred_shape_never_moves_the_suggested_effort()
    {
        // Shape is a downstream axis, not a tier input: a question and a typo fix are both cheap.
        (await ClassifyAsync("Explain how the retry loop works")).SuggestedEffort.ShouldBe(TaskEffortModes.Quick);
        (await ClassifyAsync("Fix a typo")).SuggestedEffort.ShouldBe(TaskEffortModes.Quick);
    }

    [Fact]
    public void Confidence_cap_is_strictly_below_the_confirm_floor()
    {
        // Pinned: the cap that enforces the always-confirm invariant must sit below the policy floor.
        HeuristicEffortClassifier.ConfidenceCap.ShouldBeLessThan(EffortPolicy.ConfirmConfidenceFloor);
    }
}
