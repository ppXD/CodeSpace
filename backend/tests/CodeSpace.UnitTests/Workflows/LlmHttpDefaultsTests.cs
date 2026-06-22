using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the LLM HTTP transport budget: the env-var NAME (Rule 8 — an operator tunes a slow gateway via it; a rename is
/// a silent deployment break) and the resolver (a positive integer wins; anything else falls to the generous default
/// — never 0/negative which would instantly cancel every call).
/// </summary>
[Trait("Category", "Unit")]
public class LlmHttpDefaultsTests
{
    [Fact]
    public void Timeout_env_var_name_is_pinned()
    {
        LlmHttpDefaults.RequestTimeoutSecondsEnvVar.ShouldBe("CODESPACE_LLM_HTTP_TIMEOUT_SECONDS");
    }

    [Fact]
    public void Default_is_generous_so_a_slow_model_is_not_guillotined()
    {
        LlmHttpDefaults.DefaultRequestTimeoutSeconds.ShouldBe(600);
    }

    [Theory]
    [InlineData("900", 900)]      // a valid positive override wins
    [InlineData("1", 1)]
    [InlineData(null, 600)]       // unset → default
    [InlineData("", 600)]         // blank → default
    [InlineData("nan", 600)]      // non-numeric → default
    [InlineData("0", 600)]        // zero would cancel instantly → reject to default
    [InlineData("-5", 600)]       // negative → default
    public void ResolveSeconds_takes_a_positive_override_else_the_default(string? raw, int expected)
    {
        LlmHttpDefaults.ResolveSeconds(raw).ShouldBe(expected);
    }
}
