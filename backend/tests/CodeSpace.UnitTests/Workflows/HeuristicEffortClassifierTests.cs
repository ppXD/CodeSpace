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

    [Fact]
    public void Confidence_cap_is_strictly_below_the_confirm_floor()
    {
        // Pinned: the cap that enforces the always-confirm invariant must sit below the policy floor.
        HeuristicEffortClassifier.ConfidenceCap.ShouldBeLessThan(EffortPolicy.ConfirmConfidenceFloor);
    }
}
