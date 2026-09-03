using CodeSpace.Core.Persistence;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins <see cref="PersistedText"/>, the one seam that makes harness/model-produced text storable.
///
/// <para>The two rejections it exists for are NOT the same character in the same shape, which is why a single
/// character removal cannot serve both: a <c>text</c> column rejects the raw U+0000 BYTE, while a <c>jsonb</c>
/// column rejects the six-character ESCAPE (backslash-u-0000). A document carrying that escape holds no NUL
/// byte at all, so <see cref="PersistedText.Sanitize"/> would pass it through untouched and Postgres would
/// still refuse it. Both rejections are measured against real Postgres in
/// <c>AgentRunNulBytePersistenceTests</c>; these tests pin the transformation.</para>
/// </summary>
public class PersistedTextTests
{
    private const string Nul = "\u0000";

    /// <summary>The literal six characters a JSON writer emits for a NUL inside a string — NOT a NUL byte.</summary>
    private const string JsonNulEscape = @"\u0000";

    [Theory]
    [InlineData("a" + Nul + "b", "ab")]
    [InlineData(Nul + "leading", "leading")]
    [InlineData("trailing" + Nul, "trailing")]
    [InlineData(Nul, "")]
    [InlineData(Nul + Nul + Nul, "")]
    [InlineData("a" + Nul + Nul + "b", "ab")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("  keeps \t tabs \r\n and newlines  ", "  keeps \t tabs \r\n and newlines  ")]
    [InlineData("other \u0001 control \u001f chars survive", "other \u0001 control \u001f chars survive")]
    [InlineData("unicode 中文 🎉 survives", "unicode 中文 🎉 survives")]
    public void Sanitize_removes_every_nul_byte_and_alters_nothing_else(string input, string expected) =>
        PersistedText.Sanitize(input).ShouldBe(expected);

    [Fact]
    public void Sanitize_passes_null_through_as_null() => PersistedText.Sanitize(null).ShouldBeNull();

    [Fact]
    public void Sanitize_returns_the_same_instance_when_there_is_nothing_to_remove()
    {
        // The common case is EVERY event of EVERY run — it must not allocate a copy.
        const string clean = "the agent said something perfectly ordinary";

        PersistedText.Sanitize(clean).ShouldBeSameAs(clean);
    }

    [Theory]
    [InlineData(@"{""k"":""a" + JsonNulEscape + @"b""}", @"{""k"":""ab""}")]
    [InlineData(@"{""k"":""" + JsonNulEscape + @"leading""}", @"{""k"":""leading""}")]
    [InlineData(@"{""k"":""" + JsonNulEscape + @"""}", @"{""k"":""""}")]
    [InlineData(@"{""" + JsonNulEscape + @"key"":1}", @"{""key"":1}")]
    [InlineData(@"{""k"":""a" + JsonNulEscape + JsonNulEscape + @"b""}", @"{""k"":""ab""}")]
    [InlineData(@"{""k"":""plain""}", @"{""k"":""plain""}")]
    [InlineData(@"{""k"":""other escapes survive \n \t \u0001 \\""}", @"{""k"":""other escapes survive \n \t \u0001 \\""}")]
    public void SanitizeJson_removes_the_nul_escape_sequence_jsonb_rejects(string input, string expected) =>
        PersistedText.SanitizeJson(input).ShouldBe(expected);

    [Fact]
    public void SanitizeJson_passes_null_through_as_null() => PersistedText.SanitizeJson(null).ShouldBeNull();

    [Fact]
    public void SanitizeJson_also_removes_a_raw_nul_byte()
    {
        // Belt and braces: a payload assembled by hand rather than by a JSON writer can carry the raw byte, which
        // the text-level rejection would catch first. One call handles both shapes.
        PersistedText.SanitizeJson(@"{""k"":""a" + Nul + @"b""}").ShouldBe(@"{""k"":""ab""}");
    }

    [Theory]
    [InlineData(@"{""k"":""an escaped backslash \\u0000 is literal data, not an escape""}")]
    [InlineData(@"{""k"":""\\\\u0000""}")]
    public void SanitizeJson_leaves_an_escaped_backslash_followed_by_u0000_intact(string input)
    {
        // THE correctness trap. Two backslashes then u0000 is an ESCAPED BACKSLASH followed by the literal
        // characters u0000 — jsonb accepts it, because the document holds no NUL escape at all. A naive
        // Replace of the six-character sequence would eat the second backslash and leave a dangling one,
        // turning valid JSON into a parse error. Only a genuine escape — one introduced by a backslash that
        // is not itself escaped — may be removed.
        PersistedText.SanitizeJson(input).ShouldBe(input);
    }

    [Fact]
    public void SanitizeJson_removes_a_genuine_escape_that_follows_an_escaped_backslash()
    {
        // An escaped backslash, then a REAL NUL escape: the second must go, the first pair must stay.
        PersistedText.SanitizeJson(@"{""k"":""\\" + JsonNulEscape + @"""}").ShouldBe(@"{""k"":""\\""}");
    }

    [Fact]
    public void SanitizeJson_returns_the_same_instance_when_there_is_nothing_to_remove()
    {
        const string clean = @"{""command"":""npm test"",""exitCode"":0}";

        PersistedText.SanitizeJson(clean).ShouldBeSameAs(clean);
    }
}
