using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class LlmContextWindowBudgetTests
{
    [Fact]
    public void Unknown_capacity_never_guesses_that_compaction_is_required()
    {
        var request = Request("large prompt");

        LlmContextWindowBudget.RequiresCompaction(request, contextWindowTokens: null).ShouldBeFalse();
    }

    [Fact]
    public void Budget_counts_system_user_schema_and_reserved_output_before_the_provider_call()
    {
        var request = Request(new string('x', 12_000));
        var estimatedInput = LlmContextWindowBudget.EstimateInputTokens(request);
        var justTooSmall = estimatedInput + request.MaxOutputTokens!.Value;

        LlmContextWindowBudget.RequiresCompaction(request, justTooSmall).ShouldBeTrue();
        LlmContextWindowBudget.RequiresCompaction(request, int.MaxValue).ShouldBeFalse();
    }

    [Fact]
    public void Multilingual_text_is_estimated_from_utf8_bytes_instead_of_utf16_characters()
    {
        var ascii = Request(new string('a', 1_000));
        var han = Request(new string('任', 1_000));

        LlmContextWindowBudget.EstimateInputTokens(han).ShouldBeGreaterThan(LlmContextWindowBudget.EstimateInputTokens(ascii));
    }

    private static StructuredLLMCompletionRequest Request(string userPrompt) => new()
    {
        Model = "opaque-model-id",
        SystemPrompt = "system",
        UserPrompt = userPrompt,
        JsonSchema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}}}""").RootElement,
        MaxOutputTokens = 4096,
    };
}
