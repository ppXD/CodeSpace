using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Health.Checks;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>
/// The git floor, reported. A git before 2.31 ignores the variable the credential helper is configured through, so every
/// tokened clone, fetch and push fails as "could not read Username" — a failure that looks like a bad token. The check turns
/// that into Degraded with the cause, and never into a refusal to start.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitVersionHealthCheckTests
{
    [Fact]
    public void The_check_is_named_git_in_the_report() => GitVersionHealthCheck.Name.ShouldBe("git");

    [Theory]
    [InlineData("git version 2.39.5")]
    [InlineData("git version 2.31.0")]
    public void A_git_that_reads_GIT_CONFIG_COUNT_is_healthy(string output)
    {
        var result = GitVersionHealthCheck.Assess(Reading(output));

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description.ShouldNotBeNull().ShouldContain(GitVersion.Parse(output)!.Value.ToString());
        result.Data["version"].ShouldBe(GitVersion.Parse(output)!.Value.ToString());
        result.Data["minimum"].ShouldBe("2.31.0");
    }

    [Fact]
    public void A_git_before_2_31_is_degraded_and_says_why()
    {
        var result = GitVersionHealthCheck.Assess(Reading("git version 2.30.2"));

        result.Status.ShouldBe(HealthStatus.Degraded, "Degraded keeps /health/ready at 200: an orchestrator must not restart a pod over this");
        result.Description.ShouldNotBeNull().ShouldContain("2.30.2");
        result.Description.ShouldContain("2.31");
        result.Description.ShouldContain("GIT_CONFIG_COUNT");
        result.Description.ShouldContain("could not read Username", customMessage: "it names the symptom an operator would otherwise chase");
        result.Data["version"].ShouldBe("2.30.2");
    }

    [Theory]
    [InlineData("git could not be run: Win32Exception")]
    [InlineData("something git never prints")]
    public void A_git_whose_release_cannot_be_read_is_degraded_with_what_was_seen(string detail)
    {
        var result = GitVersionHealthCheck.Assess(new GitReading(null, detail));

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain(detail);
        result.Data.ShouldNotContainKey("version");
    }

    [Fact]
    public async Task The_check_reads_the_hosts_git()
    {
        using var _ = HostGit.Use(Reading("git version 2.30.2"));

        var result = await new GitVersionHealthCheck().CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    private static GitReading Reading(string output) => new(GitVersion.Parse(output), output);
}
