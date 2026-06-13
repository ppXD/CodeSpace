using CodeSpace.Core.Services.Agents.Mcp;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the per-run MCP token (<see cref="McpRunToken"/>): <see cref="McpRunToken.Mint"/> yields a unique, url-safe
/// opaque string (no <c>+</c> / <c>/</c> / <c>=</c> so it survives an env var / a single line unaltered), and
/// <see cref="McpRunToken.Matches"/> accepts the exact token while rejecting a different one, a length mismatch, and
/// the empty-vs-non-empty case. Tier 🟢: real production class, pure in-memory.
/// </summary>
[Trait("Category", "Unit")]
public class McpRunTokenTests
{
    [Fact]
    public void Mint_yields_unique_url_safe_tokens()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => McpRunToken.Mint()).ToArray();

        tokens.Distinct().Count().ShouldBe(100, customMessage: "every mint must be unique (256-bit CSPRNG)");

        foreach (var token in tokens)
        {
            token.ShouldNotBeNullOrEmpty();
            token.ShouldNotContain("+", customMessage: "base64url must not contain '+'");
            token.ShouldNotContain("/", customMessage: "base64url must not contain '/'");
            token.ShouldNotContain("=", customMessage: "base64url must be unpadded");
        }
    }

    [Theory]
    [InlineData("abc", "abc", true)]            // equal → match
    [InlineData("abc", "abd", false)]           // same length, different content → no match
    [InlineData("abc", "abcd", false)]          // length mismatch → no match (constant time)
    [InlineData("abc", "ab", false)]            // shorter → no match
    [InlineData("", "", true)]                  // empty vs empty → match (the degenerate case)
    [InlineData("abc", "", false)]              // non-empty vs empty → no match
    public void Matches_is_exact(string expected, string presented, bool shouldMatch) =>
        McpRunToken.Matches(expected, presented).ShouldBe(shouldMatch);

    [Fact]
    public void A_freshly_minted_token_matches_itself()
    {
        var token = McpRunToken.Mint();

        McpRunToken.Matches(token, token).ShouldBeTrue();
        McpRunToken.Matches(token, McpRunToken.Mint()).ShouldBeFalse(customMessage: "a different mint must not match");
    }
}
