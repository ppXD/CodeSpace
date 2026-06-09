using CodeSpace.Core.Services.Providers.Source;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Source;

[Trait("Category", "Unit")]
public class CommitSummaryTextTests
{
    [Theory]
    [InlineData("one line", "one line")]
    [InlineData("first\nsecond", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData("  padded  \nrest", "padded")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FirstLine_takes_the_trimmed_first_line(string? message, string expected) =>
        CommitSummaryText.FirstLine(message).ShouldBe(expected);

    [Theory]
    [InlineData("abcdef1234567890", "abcdef1")]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortSha_takes_first_seven(string? sha, string expected) =>
        CommitSummaryText.ShortSha(sha).ShouldBe(expected);
}
