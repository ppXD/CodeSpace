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
    [Theory]
    // The capability token: 256-bit, and it rides an env var and a single wire line, so it must be url-safe.
    [InlineData(false, 43)]
    // The path id: 128-bit, and it becomes a FILENAME inside an AF_UNIX path whose total length is capped — so it is
    // shorter on purpose, and the same url-safe alphabet keeps it filename-safe on every platform.
    [InlineData(true, 22)]
    public void Mint_yields_unique_url_safe_values(bool pathId, int expectedLength)
    {
        var minted = Enumerable.Range(0, 100).Select(_ => pathId ? McpRunToken.MintPathId() : McpRunToken.Mint()).ToArray();

        minted.Distinct().Count().ShouldBe(100, customMessage: "every mint must be unique (CSPRNG)");

        foreach (var value in minted)
        {
            value.Length.ShouldBe(expectedLength, customMessage: "a shorter value than the pinned length means fewer random bits than the doc claims");
            value.ShouldNotContain("+", customMessage: "base64url must not contain '+'");
            value.ShouldNotContain("/", customMessage: "base64url must not contain '/' — a path id containing one would silently split into two path segments");
            value.ShouldNotContain("=", customMessage: "base64url must be unpadded");
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
