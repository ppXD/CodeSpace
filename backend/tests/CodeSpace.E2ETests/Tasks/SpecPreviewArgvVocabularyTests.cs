using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// Pins <see cref="RealModelSpecPreviewE2ETests.ArgvDeviation"/> — the ONE thing the explicit-argv real-model arm still
/// gates on once exact-shape equality is gone.
///
/// <para>The arm used to require <c>proposal.Argv.SequenceEqual(["report-proof", "--label", "Q4 Δ", ""])</c>. Across
/// seven live runs the grounding rule it exists to measure passed every time (Supported / UserExplicit / no repository
/// inferred / no execution claimed) while the model returned the argv as ONE shell-joined token in roughly two thirds
/// of attempts — so the arm reported a red about JSON TOKENISATION, which
/// <c>CompileTaskSpecResult.AcceptanceChecks</c> explicitly defers to the route adapter. Verbatim preservation of
/// empty / whitespace / Unicode tokens is pinned MODEL-FREE in <c>TaskSpecAcceptanceEvidenceTests</c>, so nothing is
/// lost by letting the live arm tolerate both shapes.</para>
///
/// <para>What must NOT be tolerated is a proposal that dropped a word the user wrote or added one they never did — the
/// difference between "the model read the goal" and "the model wrote its own command". That is what the helper decides,
/// and it decides it with no model, no database and no secrets, so it runs on the ORDINARY E2E gate (every PR) rather
/// than the real-model lane it protects — the same placement <c>RealModelLaneBoundsTests</c> uses for the same reason.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SpecPreviewArgvVocabularyTests
{
    [Theory]
    // BOTH shapes pass. The 4-token shape is the literal the goal names; the joined shape is what a live model returns
    // most of the time. Neither one lost a word or invented one, which is the whole question.
    [InlineData(new[] { "report-proof", "--label", "Q4 Δ", "" }, "")]
    [InlineData(new[] { "report-proof --label 'Q4 Δ' ''" }, "")]
    [InlineData(new[] { "report-proof --label \"Q4 Δ\" \"\"" }, "")]
    // The empty argument is INVISIBLE once tokens are flattened, so it cannot be measured here and deliberately is not
    // — TaskSpecAcceptanceEvidenceTests pins its preservation model-free, on the compiler itself.
    [InlineData(new[] { "report-proof --label 'Q4 Δ'" }, "")]
    // Invented: a shell wrapper the user never asked for is exactly the "the model wrote its own command" failure.
    [InlineData(new[] { "sh", "-c", "report-proof --label 'Q4 Δ' ''" }, "invented [sh | -c]")]
    [InlineData(new[] { "report-proof", "--label", "Q4 Δ", "", "--verbose" }, "invented [--verbose]")]
    // Lost: the Unicode label is the proof the goal was READ. A model that drops it is guessing, however plausible the
    // rest looks, and a model that transliterates it both loses and invents.
    [InlineData(new[] { "report-proof", "--label", "" }, "lost [Q4 Δ]")]
    [InlineData(new[] { "report-proof", "--label", "Q4 Delta" }, "lost [Q4 Δ], invented [Delta]")]
    [InlineData(new[] { "npm", "test" }, "lost [report-proof | --label | Q4 Δ], invented [npm | test]")]
    public void A_proposal_deviates_only_by_losing_a_requested_word_or_adding_one_the_goal_never_named(string[] argv, string expectedDeviation)
    {
        RealModelSpecPreviewE2ETests.ArgvDeviation(argv, RealModelSpecPreviewE2ETests.ExplicitArgv).ShouldBe(expectedDeviation,
            customMessage: $"the live arm gates on this string being empty, so a wrong verdict here either reds the blessed wire over JSON tokenisation or greens an invented command. Flattened input: '{string.Join(" ", argv)}'");
    }

    [Fact]
    public void The_vocabulary_is_derived_from_the_argv_the_goal_states()
    {
        // Rule 8's spirit: the allowed vocabulary is DERIVED, never hand-listed, so it cannot drift from the goal. If
        // this literal ever changes the goal text changed with it — and the seven-run live evidence behind the
        // tolerant assertion no longer describes the same stimulus.
        RealModelSpecPreviewE2ETests.ExplicitArgv.ShouldBe(new[] { "report-proof", "--label", "Q4 Δ", "" });
    }
}
