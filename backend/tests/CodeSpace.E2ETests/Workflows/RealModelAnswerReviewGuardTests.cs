using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Pins C1's answer-review arm (<see cref="RealModelAnswerReviewE2ETests"/>) against its own defect. Run 34015323751
/// — the first-ever measurement of that arm — found its goal said "do not create or edit any files" while its
/// acceptance contract demanded <see cref="RealModelAnswerReviewE2ETests.AnswerFilePath"/> exist: unsatisfiable by
/// construction, so every attempt re-graded Failed before the Gate review the arm exists to test ever ran.
///
/// <para>A pure string check (no DB, no live model) tagged to run on the ordinary <c>Surface=Engine</c> E2E gate —
/// the goal and the declared artifact must never diverge again, and that has to be caught on EVERY PR, not only on
/// the real-model lane it guards.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelAnswerReviewGuardTests
{
    [Fact]
    public void The_arms_goal_asks_for_the_artifact_its_acceptance_grades()
    {
        RealModelAnswerReviewE2ETests.AnswerReviewGoal.ShouldContain(RealModelAnswerReviewE2ETests.AnswerFilePath,
            customMessage: "the goal must ask the agent to write the exact file the ArtifactPresent oracle checks for — otherwise the acceptance contract is unsatisfiable by construction and the run re-grades Failed before the review gate this arm exists to test ever executes");
    }
}
