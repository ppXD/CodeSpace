using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Health;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The release a host's <c>git</c> reports. It decides one thing: whether the env-only credential helper
/// (<c>GIT_CONFIG_COUNT</c>, git 2.31 and later) can reach this git at all, so the parse has to cope with what real
/// distributions print and answer "unknown" — never a guess — for anything else.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitVersionTests
{
    [Theory]
    [InlineData("git version 2.39.5", 2, 39, 5)]
    [InlineData("git version 2.39.5\n", 2, 39, 5)]
    [InlineData("git version 2.31.0", 2, 31, 0)]
    [InlineData("git version 2.30.2", 2, 30, 2)]
    [InlineData("git version 2.31", 2, 31, 0)]
    [InlineData("git version 2.39.5 (Apple Git-154)", 2, 39, 5)]
    [InlineData("git version 2.43.0.windows.1", 2, 43, 0)]
    [InlineData("git version 2.45.0-rc1", 2, 45, 0)]
    public void It_reads_the_release_out_of_git_version_output(string output, int major, int minor, int patch) =>
        GitVersion.Parse(output).ShouldBe(new GitVersion(major, minor, patch));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fatal: not a git repository")]
    [InlineData("git version")]
    [InlineData("git version two.thirty")]
    [InlineData("2.39.5")]
    public void Anything_else_is_unknown_rather_than_a_guess(string? output) => GitVersion.Parse(output).ShouldBeNull();

    [Theory]
    [InlineData("2.30.2", false)]
    [InlineData("2.30.99", false)]
    [InlineData("2.31.0", true)]
    [InlineData("2.31.1", true)]
    [InlineData("2.39.5", true)]
    [InlineData("3.0.0", true)]
    [InlineData("1.99.9", false)]
    public void It_is_at_least_2_31_from_exactly_2_31(string release, bool expected)
    {
        var version = GitVersion.Parse($"git version {release}")!.Value;

        version.IsAtLeast(TokenedGitCommand.MinimumGit).ShouldBe(expected);
    }

    [Fact]
    public void The_minimum_is_the_release_that_introduced_GIT_CONFIG_COUNT() => TokenedGitCommand.MinimumGit.ShouldBe(new GitVersion(2, 31, 0));

    [Fact]
    public void It_prints_as_the_dotted_release() => new GitVersion(2, 39, 5).ToString().ShouldBe("2.39.5");

    [Fact]
    public void The_host_probe_parses_what_this_machines_git_actually_prints()
    {
        // Through the real reader: the fixtures above are what I believe distributions print; this is what one does.
        var reading = HostGit.Current;

        if (reading.Version is null && reading.Detail.StartsWith("git could not be run", StringComparison.Ordinal)) return;   // no git here; cross-host dotnet test stays clean

        reading.Version.ShouldNotBeNull($"git is installed but its output was not understood: '{reading.Detail}'");
        reading.Detail.ShouldStartWith("git version ");
    }
}
