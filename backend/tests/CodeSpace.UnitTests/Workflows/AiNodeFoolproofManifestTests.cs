using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The AI-family nodes (llm.complete, plan.author, plan.confirm) opt into the foolproof editor layer: an
/// x-intent one-liner at the top of the inspector, and their behaviour-fork enum rendered as radioCards where
/// EVERY option carries a plain-language consequence. These are presentation-only hints the engine ignores —
/// the config VALUE shape is unchanged (radioCards stores the SAME enum string the &lt;select&gt; did, and the
/// reviewMode reader already tolerates that string). Pinning them makes a manifest refactor that silently drops
/// the intent line or an option's consequence a loud failure instead of a quiet UX regression.
/// </summary>
[Trait("Category", "Unit")]
public class AiNodeFoolproofManifestTests
{
    // Each node's Manifest is a field initializer parsed at construction (only static SchemaBuilder calls), so
    // null! constructor deps are safe for reading the schema. The third column is the node's behaviour-fork enum.
    public static IEnumerable<object[]> AiNodes()
    {
        yield return new object[] { "llm.complete", new LlmCompleteNode(null!, null!).Manifest.ConfigSchema, "provider" };
        yield return new object[] { "plan.author", new PlanAuthorNode(null!).Manifest.ConfigSchema, "reviewMode" };
        yield return new object[] { "plan.confirm", new PlanConfirmNode(null!).Manifest.ConfigSchema, "reviewMode" };
    }

    [Theory]
    [MemberData(nameof(AiNodes))]
    public void Config_root_declares_a_plain_language_intent(string node, JsonElement config, string forkField)
    {
        config.GetProperty("properties").TryGetProperty(forkField, out _).ShouldBeTrue($"{node} should declare its '{forkField}' behaviour-fork field");

        config.TryGetProperty("x-intent", out var intent).ShouldBeTrue($"{node} config must declare an x-intent line");
        intent.GetString().ShouldNotBeNullOrWhiteSpace();

        // Every interpolated token that can be unset needs a muted-prompt fallback so the line never renders blank.
        config.TryGetProperty("x-intentPlaceholders", out var prompts).ShouldBeTrue($"{node} intent must declare x-intentPlaceholders");
        prompts.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Theory]
    [MemberData(nameof(AiNodes))]
    public void Behaviour_fork_renders_as_radiocards_and_explains_every_option(string node, JsonElement config, string forkField)
    {
        var prop = config.GetProperty("properties").GetProperty(forkField);

        prop.GetProperty("x-control").GetString().ShouldBe("radioCards", $"{node}.{forkField} is a behaviour fork — render it as stacked cards, not a bare dropdown");

        prop.TryGetProperty("x-optionConsequence", out var cons).ShouldBeTrue($"{node}.{forkField} must declare x-optionConsequence so no option is a mystery");
        foreach (var value in prop.GetProperty("enum").EnumerateArray())
        {
            cons.TryGetProperty(value.ToString(), out var consequence).ShouldBeTrue($"{node}.{forkField} option '{value}' has no consequence line");
            consequence.GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    // The provider feeds ILLMClientRegistry.Resolve — only the three registered ILLMProviderModule wires resolve.
    // "OpenRouter"/"Ollama" are OpenAI-wire credential TAGS, not modules, so they must NOT appear here (they would
    // fail to resolve). Pin the exact set so a well-meaning "add every credential tag" edit can't strand a run.
    [Fact]
    public void LlmComplete_provider_enum_is_exactly_the_registered_wire_modules()
    {
        var provider = new LlmCompleteNode(null!, null!).Manifest.ConfigSchema.GetProperty("properties").GetProperty("provider");

        var values = provider.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray();
        values.ShouldBe(new[] { "Anthropic", "OpenAI", "Custom" });
    }

    // The Model field pins the bare model-id STRING; a "poolModel" selector lists the team's pool for the chosen
    // provider and stores that string — NOT the "credentialedModel" ROW id (a Guid the model-id pin never matches).
    [Fact]
    public void LlmComplete_model_is_a_pool_scoped_picker()
    {
        var model = new LlmCompleteNode(null!, null!).Manifest.ConfigSchema.GetProperty("properties").GetProperty("model");

        model.GetProperty("x-selector").GetString().ShouldBe("poolModel");
        model.GetProperty("type").GetString().ShouldBe("string");
    }
}
