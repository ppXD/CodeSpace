using CodeSpace.Core.Services.Agents.Workspace;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="AgentCloneGitCommand"/> — the pure spec builder (no git, no runner) for every command the platform runs
/// over an agent-writable clone. Pins the hardening argv exactly, so a dropped or re-spelled flag is a visible test
/// failure rather than a silent re-opening of the hook / fsmonitor / textconv / external-diff vectors the integration
/// tests (<c>AgentCloneGitHookIsolationFlowTests</c>) prove closed against real git.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentCloneGitCommandTests
{
    private static readonly string[] Hardening = { "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=" };

    [Fact]
    public void The_hardening_values_are_pinned_literally()
    {
        // /dev/null, not an empty value: git resolves `core.hooksPath=` to the filesystem root (`/pre-commit`), while
        // `/dev/null/<hook>` can never exist. An empty fsmonitor, not `false`: git before 2.36 reads core.fsmonitor only
        // as a program path, so `false` runs a PATH-resolved `false` on every index refresh (seen on git 2.33).
        AgentCloneGitCommand.HardeningConfig.ShouldBe(Hardening);
    }

    [Theory]
    [InlineData("add", "-A")]
    [InlineData("checkout", "-B", "codespace/run")]
    [InlineData("rev-parse", "HEAD")]
    [InlineData("-c", "commit.gpgsign=false", "-c", "user.name=CodeSpace", "commit", "-m", "Agent run")]
    public void A_non_diff_command_gets_only_the_config_prefix(params string[] args)
    {
        // A commit starts with its own -c identity flags, not the subcommand: the hardening still goes first and no
        // diff-only flag is added.
        AgentCloneGitCommand.HardenedArgs(args).ShouldBe(Hardening.Concat(args));
    }

    [Theory]
    [InlineData("--cached", "--no-color", "BASE")]
    [InlineData("--cached", "--name-only", "BASE")]
    [InlineData("--cached", "--numstat", "BASE")]
    [InlineData("--quiet", "BASE", "HEAD")]
    public void A_diff_gets_the_driver_suppressions_right_after_the_subcommand(params string[] diffArgs)
    {
        var args = new[] { "diff" }.Concat(diffArgs).ToArray();

        AgentCloneGitCommand.HardenedArgs(args).ShouldBe(Hardening.Concat(new[] { "diff", "--no-ext-diff", "--no-textconv" }).Concat(diffArgs));
    }

    [Fact]
    public void Build_severs_the_network_and_carries_no_credential()
    {
        var spec = AgentCloneGitCommand.Build(new[] { "add", "-A" }, "/work/clone", new[] { "/ro/source" }, 120);

        spec.Command.ShouldBe("git");
        spec.Args.ShouldBe(Hardening.Concat(new[] { "add", "-A" }));
        spec.AllowNetwork.ShouldBeFalse("an agent-writable .git never meets the host network");
        spec.EgressAllowlist.ShouldBeNull();
        spec.Environment.ShouldBeEmpty("no clone token, askpass or credential-helper value reaches this git");
        spec.WorkingDirectory.ShouldBe("/work/clone");
        spec.ReadOnlyPaths.ShouldBe(new[] { "/ro/source" });
        spec.TimeoutSeconds.ShouldBe(120);
    }
}
