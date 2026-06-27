using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure URL→pack-name + URL→kind derivation the commit path stamps onto a new <c>Pack</c> row.
/// </summary>
[Trait("Category", "Unit")]
public class PackImportNamingTests
{
    [Theory]
    [InlineData("https://github.com/obra/superpowers", "obra/superpowers")]
    [InlineData("https://github.com/obra/superpowers.git", "obra/superpowers")]
    [InlineData("https://github.com/obra/superpowers/", "obra/superpowers")]
    [InlineData("https://gitlab.com/group/sub/repo", "group/sub/repo")]
    [InlineData("https://github.com", "github.com")]
    public void DerivePackName_takes_owner_repo_and_strips_dot_git(string url, string expected)
    {
        PackImportService.DerivePackName(url).ShouldBe(expected);
    }

    [Fact]
    public void DerivePackName_passes_a_non_url_through_unchanged()
    {
        PackImportService.DerivePackName("not a url").ShouldBe("not a url");
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", PackKind.Github)]
    [InlineData("https://GITHUB.com/owner/repo", PackKind.Github)]
    [InlineData("https://github.com./owner/repo", PackKind.Github)]   // trailing-dot FQDN must normalize, not bypass
    [InlineData("https://gitlab.com/owner/repo", PackKind.GitUrl)]
    [InlineData("https://git.example.com/owner/repo", PackKind.GitUrl)]
    public void DeterminePackKind_is_github_only_for_github_host(string url, PackKind expected)
    {
        PackImportService.DeterminePackKind(url).ShouldBe(expected);
    }
}
