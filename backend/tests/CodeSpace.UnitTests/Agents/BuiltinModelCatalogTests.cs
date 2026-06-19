using CodeSpace.Core.Services.Agents.ModelCredentials;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the in-code structured-output seed + its env override. The seed is the library-authored "high trust" source;
/// the env override (Rule 8) is how an air-gapped operator declares a custom / gateway model's structured support
/// WITHOUT a code change. Tests in one class run sequentially, so the env-mutating cases don't race each other.
/// </summary>
[Trait("Category", "Unit")]
public class BuiltinModelCatalogTests
{
    [Fact]
    public void A_known_structured_model_supports_structured_output()
    {
        BuiltinModelCatalog.SupportsStructuredOutput("claude-opus-4-8").ShouldBeTrue();
        BuiltinModelCatalog.SupportsStructuredOutput("claude-sonnet-4-6").ShouldBeTrue();
    }

    [Fact]
    public void A_codex_coding_model_is_not_structured_capable()
    {
        // Codex-class models are tool-use coding agents, not structured-JSON providers — they must NOT pass the decider gate.
        BuiltinModelCatalog.SupportsStructuredOutput("gpt-5.4-codex").ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-unknown/custom-model")]
    public void An_unknown_or_blank_model_takes_the_false_floor(string? modelId)
    {
        BuiltinModelCatalog.SupportsStructuredOutput(modelId).ShouldBeFalse();
    }

    [Fact]
    public void The_env_override_declares_a_custom_models_structured_support()
    {
        WithEnv("my-co/coder; some-gateway/opus", () =>
        {
            BuiltinModelCatalog.SupportsStructuredOutput("my-co/coder").ShouldBeTrue();
            BuiltinModelCatalog.SupportsStructuredOutput("some-gateway/opus").ShouldBeTrue();

            // An id NOT in the override falls back to the seed (still false for unknowns, still true for seeded ids).
            BuiltinModelCatalog.SupportsStructuredOutput("another/unknown").ShouldBeFalse();
            BuiltinModelCatalog.SupportsStructuredOutput("claude-opus-4-8").ShouldBeTrue();
        });
    }

    [Fact]
    public void A_malformed_env_entry_is_skipped_and_lookup_never_throws()
    {
        WithEnv(";  ; good-model ;", () =>
        {
            BuiltinModelCatalog.SupportsStructuredOutput("good-model").ShouldBeTrue();
            BuiltinModelCatalog.SupportsStructuredOutput("gpt-5.4-codex").ShouldBeFalse("the seed is intact for ids not in the override");
        });
    }

    [Fact]
    public void Structured_models_env_var_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who declared custom-model structured support via env. Hard-pin (Rule 8).
        BuiltinModelCatalog.StructuredModelsEnvVar.ShouldBe("CODESPACE_STRUCTURED_OUTPUT_MODELS");
    }

    private static void WithEnv(string value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(BuiltinModelCatalog.StructuredModelsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BuiltinModelCatalog.StructuredModelsEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(BuiltinModelCatalog.StructuredModelsEnvVar, original);
        }
    }
}
