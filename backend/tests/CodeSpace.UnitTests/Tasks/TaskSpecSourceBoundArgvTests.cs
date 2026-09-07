using System.Text.Json;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

[Trait("Category", "Unit")]
public sealed class TaskSpecSourceBoundArgvTests
{
    [Theory]
    [InlineData("verifier-a", "資料 Δ", "")]
    [InlineData("工具-b", "\"quoted\"", " ")]
    [InlineData("random-tool-c", "\\backslash", "line\nbreak")]
    public void A_model_selected_literal_reference_preserves_source_tokens_instead_of_regenerating_them(string executable, string label, string last)
    {
        var expected = new[] { executable, label, last };
        var quote = JsonSerializer.Serialize(expected);
        var context = Context("Use precisely this validation argv: " + quote);
        var compilation = Compilation(new("goal", quote, "json-argv"));
        var suggestion = TaskSpecCompiler.ToSuggestion(compilation, context, Review(context, "supported"))!;
        suggestion.AcceptanceChecks.ShouldBe(expected);
        suggestion.AcceptanceProposal!.Argv.ShouldBe(expected);
        suggestion.AcceptanceProposal.Status.ShouldBe(TaskSpecEvidenceStatus.Supported);
    }

    [Theory]
    [InlineData("foreign-source", "[\"check\"]", "json-argv")]
    [InlineData("goal", "[\"invented\"]", "json-argv")]
    [InlineData("goal", "[\"check\"]", "shell")]
    [InlineData("goal", "[]", "json-argv")]
    [InlineData("goal", "[null]", "json-argv")]
    [InlineData("goal", "[\"\",\"arg\"]", "json-argv")]
    [InlineData("goal", "[\"check\",\"bad\\u0000arg\"]", "json-argv")]
    [InlineData("goal", "[\"check\",1]", "json-argv")]
    [InlineData("goal", "[\"check\"] garbage", "json-argv")]
    public void An_invalid_reference_cannot_fall_back_to_a_generated_command_even_when_the_reviewer_says_supported(string sourceId, string quote, string encoding)
    {
        var context = Context("Available source: " + (quote == "[\"invented\"]" ? "[\"check\"]" : quote));
        var suggestion = TaskSpecCompiler.ToSuggestion(Compilation(new(sourceId, quote, encoding)), context, Review(context, "supported"))!;
        suggestion.AcceptanceChecks.ShouldBeEmpty();
        suggestion.AcceptanceProposal!.Status.ShouldBe(TaskSpecEvidenceStatus.Unknown);
        suggestion.AcceptanceProposal.Reason.ShouldContain("source reference");
    }

    [Fact]
    public void Literal_integrity_does_not_override_an_independent_negation_assessment()
    {
        const string quote = "[\"arbitrary-tool\",\"\"]";
        var context = Context("The old instructions said " + quote + ". Do not use that check for this task.");
        var suggestion = TaskSpecCompiler.ToSuggestion(Compilation(new("goal", quote, "json-argv")), context, Review(context, "contradicted"))!;
        suggestion.AcceptanceChecks.ShouldBeEmpty();
        suggestion.AcceptanceProposal!.Status.ShouldBe(TaskSpecEvidenceStatus.Contradicted);
        suggestion.AcceptanceProposal.Argv.ShouldBe(new[] { "arbitrary-tool", "" });
    }

    private static TaskSpecCompilation Compilation(TaskSpecArgvSource source) => new() { AcceptanceArgvSource = source, AcceptanceChecks = ["regenerated-and-wrong"], AcceptanceCriteria = ["The result satisfies the request."] };
    private static TaskSpecEvidenceContext Context(string goal) => TaskSpecEvidenceContext.TaskOnly(new(Guid.NewGuid(), goal, null), TaskSpecRepositoryState.NotRequested, "No repository requested.");
    private static TaskSpecReview Review(TaskSpecEvidenceContext context, string support) => new() { Source = "user-explicit", Support = support, Reason = "Independent meaning assessment, not execution.", Citations = [new("goal", context.Request.Goal)] };
}
