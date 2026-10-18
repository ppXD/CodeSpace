using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Health;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A tokened command carries its credential in a helper that <c>GIT_CONFIG_COUNT</c> configures, and a git older than 2.31
/// ignores that variable: the remote asks for a credential, nothing answers, and git says it "could not read Username".
/// That sentence names no cause an operator can act on. When — and only when — the host's git is known to be too old
/// and the command was tokened, the failure says so.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TokenedGitOldGitFailureTests
{
    private const string NoUsername = "fatal: could not read Username for 'https://gitlab.example.test': terminal prompts disabled";

    [Theory]
    [InlineData(NoUsername)]
    [InlineData("fatal: could not read Username for 'https://gitlab.example.test': No such device or address")]
    [InlineData("fatal: could not read Password for 'https://x-access-token@gitlab.example.test': terminal prompts disabled")]
    [InlineData("remote: HTTP Basic: Access denied\nfatal: could not read Username for 'https://gitlab.example.test': terminal prompts disabled\n")]
    public void A_tokened_command_refused_for_want_of_a_credential_by_a_git_before_2_31_fails_with_the_cause(string stderr)
    {
        var failure = TokenedGitCommand.OldGitFailure(tokened: true, stderr, Reading("git version 2.30.2"));

        var typed = failure.ShouldBeOfType<GitCredentialHelperUnsupportedException>();

        typed.Version.ShouldBe(new GitVersion(2, 30, 2));
        typed.Message.ShouldContain("2.30.2", customMessage: "the message names the git the host has");
        typed.Message.ShouldContain("2.31", customMessage: "and the release that can carry the credential");
        typed.Message.ShouldContain("GIT_CONFIG_COUNT", customMessage: "and why an older one cannot");
        typed.Message.ShouldContain("Upgrade", customMessage: "and what to do about it");
        typed.Message.ShouldNotContain("could not read", customMessage: "the cryptic sentence is what this replaces");
    }

    [Fact]
    public void The_typed_failure_is_still_a_workspace_failure_so_every_existing_catch_site_handles_it()
    {
        var failure = TokenedGitCommand.OldGitFailure(tokened: true, NoUsername, Reading("git version 2.30.2"));

        failure.ShouldBeAssignableTo<WorkspaceException>("the run executor, the integrate nodes and the grader catch WorkspaceException; a sibling type would escape them");
        ((IFailure)failure!).Code.ShouldBe(FailureCodes.WorkspaceUnresolvable, "the wire code a client branches on is unchanged");
        ((IFailure)failure).Kind.ShouldBe(FailureKind.Unprocessable);
    }

    public static TheoryData<bool, string, string> NotTheOldGitCase => new()
    {
        { true, NoUsername, "git version 2.31.0" },    // exactly the first release that reads GIT_CONFIG_COUNT: the credential really was missing or refused
        { true, NoUsername, "git version 2.39.5" },
        { false, NoUsername, "git version 2.30.2" },   // untokened: no helper was ever going to be configured, so an old git explains nothing
        { true, "fatal: repository 'https://gitlab.example.test/a/b.git/' not found", "git version 2.30.2" },   // a different failure; an old git clones a public repository fine
        { true, "fatal: unable to access 'https://gitlab.example.test/a/b.git/': The requested URL returned error: 403", "git version 2.30.2" },
        { true, "", "git version 2.30.2" },
        { true, NoUsername, "fatal: not a git repository" },   // a git whose release is unknown cannot be blamed
    };

    [Theory]
    [MemberData(nameof(NotTheOldGitCase))]
    public void It_stays_out_of_the_way_unless_an_old_git_is_the_explanation(bool tokened, string stderr, string gitOutput) =>
        TokenedGitCommand.OldGitFailure(tokened, stderr, Reading(gitOutput)).ShouldBeNull();

    [Fact]
    public void The_host_overload_reads_the_hosts_git_and_a_test_can_stand_in_for_it()
    {
        using (HostGit.Use(Reading("git version 2.30.2")))
            TokenedGitCommand.OldGitFailure(tokened: true, NoUsername).ShouldBeOfType<GitCredentialHelperUnsupportedException>();

        using (HostGit.Use(Reading("git version 2.39.5")))
            TokenedGitCommand.OldGitFailure(tokened: true, NoUsername).ShouldBeNull();
    }

    private static GitReading Reading(string output) => new(GitVersion.Parse(output), output);
}
