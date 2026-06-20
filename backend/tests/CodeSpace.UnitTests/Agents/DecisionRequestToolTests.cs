using System.Text.Json;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the first-party <c>decision.request</c> tool (Decision substrate D2): its reserved kind (Rule 8), its
/// fail-closed input validation (a question is mandatory; option-bearing types need unique, recommendation-consistent
/// options), and that its <see cref="DecisionRequestTool.CallAsync"/> never silently no-ops (it is intercepted by the
/// handler — a direct call is a mis-wire and must surface as an error).
/// </summary>
[Trait("Category", "Unit")]
public class DecisionRequestToolTests
{
    private static readonly DecisionRequestTool Tool = new();

    [Fact]
    public void Kind_is_the_pinned_reserved_name()
    {
        // Renaming this orphans every parked decision AND breaks the ledger answer-CAS guard + the handler special-case.
        DecisionToolKinds.DecisionRequest.ShouldBe("decision.request");
        Tool.Kind.ShouldBe("decision.request");
    }

    [Fact]
    public void It_is_an_ask_not_a_gated_side_effect()
    {
        Tool.IsReadOnly.ShouldBeFalse();
        Tool.IsDestructive.ShouldBeFalse("a decision asks — it does not mutate the world");
        Tool.RequiresApproval.ShouldBeFalse("the handler special-cases it before the gate; it is never approval-gated as a tool");
    }

    [Theory]
    [InlineData("""{ "question": "ship it?" }""", true, "confirm-style ask needs no options")]
    [InlineData("""{ "question": "ship it?", "decisionType": "free_text" }""", true, "free_text needs no options")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one", "options": [ { "id": "a", "label": "A" }, { "id": "b", "label": "B" } ] }""", true, "valid choose_one")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one", "options": [ { "id": "a", "label": "A" } ], "recommendedOption": "a" }""", true, "recommendation matches an option")]
    [InlineData("""{ }""", false, "missing question")]
    [InlineData("""{ "question": "  " }""", false, "blank question")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one" }""", false, "choose_one with no options")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one", "options": [] }""", false, "choose_one with empty options")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one", "options": [ { "id": "a", "label": "A" }, { "id": "a", "label": "B" } ] }""", false, "duplicate option ids")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one", "options": [ { "id": "a", "label": "A" } ], "recommendedOption": "z" }""", false, "recommendation not an option id")]
    [InlineData("""{ "question": "pick", "decisionType": "choose_one", "options": [ { "id": "a" } ] }""", false, "option missing label")]
    public void ValidateInput(string json, bool expectedValid, string because)
    {
        var input = JsonDocument.Parse(json).RootElement;

        Tool.ValidateInput(input).IsValid.ShouldBe(expectedValid, because);
    }

    [Fact]
    public void ValidateInput_rejects_a_non_object()
    {
        Tool.ValidateInput(JsonDocument.Parse("\"nope\"").RootElement).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task CallAsync_fail_closes_because_the_handler_intercepts_the_live_path()
    {
        var result = await Tool.CallAsync(new AgentToolCall { Input = JsonDocument.Parse("{}").RootElement }, CancellationToken.None);

        result.IsError.ShouldBeTrue("a direct CallAsync is a mis-wire — it must surface an error, never silently succeed");
    }

    [Fact]
    public void Schemas_describe_the_decision_request_and_answer_shapes()
    {
        Tool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("question");
        Tool.OutputSchema.GetProperty("properties").TryGetProperty("answeredBy", out _).ShouldBeTrue();
    }
}
