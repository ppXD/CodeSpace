using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class FinalOutputReviewTests
{
    // ── EndsWithUnresolvedQuestion (the pure heuristic) ──────────────────────────

    [Theory]
    [InlineData("Should I proceed with the refactor?")]                  // single line, trailing ?
    [InlineData("I fixed the bug.\nShould I also refactor the helper?")] // multi-line, last line is the question
    [InlineData("Done.\n\nDo you want me to open a PR?")]                // blank lines between body and the closing ask
    [InlineData("Which approach do you prefer?\"")]                      // a trailing quote after the ?
    [InlineData("(should I delete the old file?)")]                      // wrapped in parens
    [InlineData("Ready. **Shall I proceed?**")]                         // markdown bold wrapper
    public void Ends_with_a_trailing_question_is_flagged(string summary) =>
        FinalOutputReview.EndsWithUnresolvedQuestion(summary).ShouldBeTrue();

    [Theory]
    [InlineData("I refactored it. Please confirm the new module boundary before I continue.")]
    [InlineData("All tests pass. Let me know which option you'd like for the API shape.")]
    [InlineData("I'm blocked — waiting for your decision on the schema.")]
    [InlineData("Would you like me to also update the docs.")]   // a hand-back phrase even without a trailing ?
    public void Closing_handback_phrase_is_flagged(string summary) =>
        FinalOutputReview.EndsWithUnresolvedQuestion(summary).ShouldBeTrue();

    [Theory]
    [InlineData("Fixed the failing billing tests and pushed the branch.")]                                  // plain statement
    [InlineData("Should I have used a switch? In the end I used a dictionary, which is cleaner.")]           // a rhetorical question, resolved in the same line
    [InlineData("Done. All 12 tests pass.")]
    [InlineData("## Next steps?")]                                                                           // markdown header ending in '?' — a heading, not a hand-back
    [InlineData("## Questions?")]
    [InlineData("Done. All tests pass.\n\n## What changed?")]                                                // header at the parting line of a multi-line summary
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData(null)]
    public void A_non_question_ending_is_not_flagged(string? summary) =>
        FinalOutputReview.EndsWithUnresolvedQuestion(summary).ShouldBeFalse();

    [Fact]
    public void A_handback_question_only_in_the_body_not_the_parting_line_is_not_flagged()
    {
        // Precision: the heuristic is scoped to the PARTING line. A question earlier in the body that the agent then
        // resolved must not trip it — otherwise every "I considered X? then did Y" success would be re-graded.
        const string summary = "Should I proceed? Actually, I went ahead and did it.\nAll done — tests green.";

        FinalOutputReview.EndsWithUnresolvedQuestion(summary).ShouldBeFalse();
    }

    // ── ReGrade (the pure re-grade) ──────────────────────────────────────────────

    [Fact]
    public void ReGrade_turns_a_question_ending_success_into_needs_review_preserving_the_work()
    {
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            Summary = "I drafted the change. Should I proceed?", ChangedFiles = new[] { "a.ts" }, Patch = "+x\n", ProducedBranch = "agent/x",
        };

        var graded = FinalOutputReview.ReGrade(result);

        graded.Status.ShouldBe(AgentRunStatus.NeedsReview);
        graded.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsReview);
        graded.ExitReason.ShouldBe("needs-review");
        graded.PendingDecisionId.ShouldBeNull("A2 carries no decision id — it's a heuristic, not a raised decision");
        graded.Summary.ShouldBe("I drafted the change. Should I proceed?");
        graded.ChangedFiles.ShouldBe(new[] { "a.ts" });
        graded.Patch.ShouldBe("+x\n");
        graded.ProducedBranch.ShouldBe("agent/x");
    }

    [Fact]
    public void ReGrade_leaves_a_clean_success_untouched()
    {
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Fixed and pushed." };

        FinalOutputReview.ReGrade(result).ShouldBeSameAs(result, "a non-question success passes through reference-unchanged");
    }

    [Fact]
    public void ReGrade_never_touches_a_non_succeeded_terminal()
    {
        // Even a Failed run whose error text ends with a question stays Failed — A2 re-grades only a would-be success.
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = "It broke. Should I retry?" };

        FinalOutputReview.ReGrade(result).ShouldBeSameAs(result);
    }

    // ── Enabled flag (Rule 8: opt-in, pinned) ────────────────────────────────────

    [Fact]
    public void Enabled_env_var_name_is_pinned()
    {
        // A rename silently disables the net for any operator who opted in. Hard-pin (Rule 8).
        FinalOutputReview.EnabledEnvVar.ShouldBe("CODESPACE_AGENT_FINAL_OUTPUT_REVIEW_ENABLED");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]   // trimmed
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]      // unset → default-OFF
    public void Enabled_is_opt_in_only_for_truthy_values(string? raw, bool expected)
    {
        var prior = Environment.GetEnvironmentVariable(FinalOutputReview.EnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(FinalOutputReview.EnabledEnvVar, raw);
            FinalOutputReview.Enabled.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FinalOutputReview.EnabledEnvVar, prior);
        }
    }
}
