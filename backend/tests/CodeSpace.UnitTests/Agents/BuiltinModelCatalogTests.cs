using CodeSpace.Core.Services.Agents.ModelCredentials;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the in-code capability seed + its env override. The seed is the library-authored "high trust" source; the
/// env override (Rule 8) is how an air-gapped operator declares a custom / gateway model's capabilities WITHOUT a
/// code change. Tests in one class run sequentially, so the env-mutating cases don't race each other.
/// </summary>
[Trait("Category", "Unit")]
public class BuiltinModelCatalogTests
{
    [Fact]
    public void A_known_strong_model_is_supervisor_recommended_and_capable()
    {
        var caps = BuiltinModelCatalog.For("claude-opus-4-8");

        caps.SupportsStructuredOutput.ShouldBeTrue();
        caps.SupportsToolUse.ShouldBeTrue();
        caps.RecommendedForSupervisor.ShouldBeTrue();
    }

    [Fact]
    public void A_codex_model_is_tool_capable_but_not_supervisor_recommended()
    {
        var caps = BuiltinModelCatalog.For("gpt-5.4-codex");

        caps.SupportsToolUse.ShouldBeTrue();
        caps.SupportsStructuredOutput.ShouldBeFalse();
        caps.RecommendedForSupervisor.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-unknown/custom-model")]
    public void An_unknown_or_blank_model_takes_the_all_false_floor(string? modelId)
    {
        var caps = BuiltinModelCatalog.For(modelId);

        caps.SupportsStructuredOutput.ShouldBeFalse();
        caps.SupportsToolUse.ShouldBeFalse();
        caps.RecommendedForSupervisor.ShouldBeFalse();
    }

    [Fact]
    public void The_env_override_declares_a_custom_models_capabilities_and_wins_over_the_seed()
    {
        WithEnv("my-co/coder=st; claude-opus-4-8=t", () =>
        {
            var custom = BuiltinModelCatalog.For("my-co/coder");
            custom.SupportsStructuredOutput.ShouldBeTrue();
            custom.SupportsToolUse.ShouldBeTrue();
            custom.RecommendedForSupervisor.ShouldBeFalse();

            // The override beats the seed for a known id too — 't' replaces opus's strong flags.
            var overridden = BuiltinModelCatalog.For("claude-opus-4-8");
            overridden.SupportsToolUse.ShouldBeTrue();
            overridden.SupportsStructuredOutput.ShouldBeFalse();
            overridden.RecommendedForSupervisor.ShouldBeFalse();
        });
    }

    [Fact]
    public void A_malformed_env_entry_is_skipped_and_lookup_never_throws()
    {
        WithEnv("=nokey;no-equals;good-model=r", () =>
        {
            BuiltinModelCatalog.For("good-model").RecommendedForSupervisor.ShouldBeTrue();
            BuiltinModelCatalog.For("gpt-5.4-codex").SupportsToolUse.ShouldBeTrue("the seed is intact for ids not in the table");
        });
    }

    [Fact]
    public void Capability_table_env_var_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who declared custom-model capabilities via env. Hard-pin (Rule 8).
        BuiltinModelCatalog.CapabilityTableEnvVar.ShouldBe("CODESPACE_MODEL_CAPABILITIES");
    }

    private static void WithEnv(string value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(BuiltinModelCatalog.CapabilityTableEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BuiltinModelCatalog.CapabilityTableEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(BuiltinModelCatalog.CapabilityTableEnvVar, original);
        }
    }
}
