using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins <see cref="SessionSummarizer.BuildUserPrompt"/> — the distillation prompt framing — without a live LLM:
/// the no-summary path asks to summarise the turns; the with-summary path bases the next distillation on the running
/// summary and folds in only the new turns; each turn renders its clean goal / result / branch via the shared reader.
/// </summary>
[Trait("Category", "Unit")]
public class SessionSummarizerPromptTests
{
    private static SessionSummarizer.TurnRow Turn(int n, string goal, string result, string? branch = null) =>
        new(n, "Success",
            branch is null ? $$"""{"summary":"{{result}}"}""" : $$"""{"summary":"{{result}}","branch":"{{branch}}"}""",
            $$"""{"goal":"{{goal}}"}""");

    [Fact]
    public void First_distillation_asks_to_summarise_the_turns_and_renders_their_fields()
    {
        var prompt = SessionSummarizer.BuildUserPrompt(existingSummary: null, new[]
        {
            Turn(1, "build login", "added auth", branch: "run-1/auth"),
            Turn(2, "add tests", "tests green"),
        });

        prompt.ShouldContain("Summarise these earlier turns", Case.Sensitive);
        prompt.ShouldNotContain("Summary so far", Case.Sensitive, "no existing summary to base on");
        prompt.ShouldContain("Asked: build login", Case.Sensitive);
        prompt.ShouldContain("Result: added auth", Case.Sensitive);
        prompt.ShouldContain("Produced branch: run-1/auth", Case.Sensitive);
        prompt.ShouldContain("Asked: add tests", Case.Sensitive);
    }

    [Fact]
    public void Incremental_distillation_bases_on_the_running_summary_and_folds_the_new_turns()
    {
        var prompt = SessionSummarizer.BuildUserPrompt(existingSummary: "Earlier: shipped auth + tests.", new[]
        {
            Turn(3, "add logout", "logout done"),
        });

        prompt.ShouldContain("Summary so far", Case.Sensitive);
        prompt.ShouldContain("Earlier: shipped auth + tests.", Case.Sensitive, "the running summary is the base");
        prompt.ShouldContain("Fold these earlier turns", Case.Sensitive);
        prompt.ShouldContain("Asked: add logout", Case.Sensitive, "the newly scrolled-out turn is folded in");
    }
}
