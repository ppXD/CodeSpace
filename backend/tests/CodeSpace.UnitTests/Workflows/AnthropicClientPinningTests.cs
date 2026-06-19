using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — env-var override escape hatch + test pinning. Operators wire their Anthropic
/// API key via this env var; renaming the constant breaks every deployment that pinned the
/// old name. This test makes the rename a deliberate, compile-time-visible decision.
/// </summary>
[Trait("Category", "Unit")]
public class AnthropicClientPinningTests
{
    [Fact]
    public void ApiKeyEnvVar_pinned()
    {
        AnthropicClient.ApiKeyEnvVar.ShouldBe("CODESPACE_ANTHROPIC_API_KEY");
    }

    [Fact]
    public void DefaultBaseUrl_is_official_anthropic_endpoint()
    {
        AnthropicClient.DefaultApiBaseUrl.ShouldBe("https://api.anthropic.com");
    }

    [Fact]
    public void AnthropicVersion_header_pinned()
    {
        // The 2023-06-01 Messages-API contract is what AnthropicClient's request body shape
        // is built against. Bumping the version means re-auditing the request body.
        AnthropicClient.AnthropicVersion.ShouldBe("2023-06-01");
    }
}
