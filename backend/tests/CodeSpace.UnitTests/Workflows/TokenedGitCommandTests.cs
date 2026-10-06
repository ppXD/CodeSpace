using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="TokenedGitCommand"/> — the one place a git command is marked as carrying a clone token. Pins the scoped
/// reset and the trace2 switch literally, and which remote URLs count as tokened; the call sites are pinned next to their
/// own classes, and <c>TokenedGitCredentialHelperFlowTests</c> proves both against real git, a real store helper, a
/// proxy whose password that helper holds, and real trace2 targets.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TokenedGitCommandTests
{
    [Theory]
    [InlineData("https://x-access-token:ghp_abc@github.com/org/repo.git", "credential.https://github.com.helper=")]
    [InlineData("http://x-access-token:t@127.0.0.1:8080/remote.git", "credential.http://127.0.0.1:8080.helper=")]
    [InlineData("https://oauth2:p%40ss%2Fword@GitLab.Example.com:8443/org/repo.git", "credential.https://gitlab.example.com:8443.helper=")]
    public void The_reset_is_scoped_to_the_tokened_remote_and_pinned_literally(string url, string reset)
    {
        // EMPTY, not false: an empty helper value clears the list collected so far, any other value adds one more helper.
        // Scoped to the remote's scheme and authority: it clears every helper git would ask about that remote, and leaves
        // the ones a proxy or another host needs. Never the userinfo — the key must not carry the token itself.
        TokenedGitCommand.CredentialHelperReset(url).ShouldBe(new[] { "-c", reset });
    }

    [Fact]
    public void Trace2_off_is_pinned_literally()
    {
        // git's own names for its three trace2 targets. The environment wins over the trace2.*Target an operator set in
        // system or global config, which git reads before any -c.
        TokenedGitCommand.TraceOff.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}").ShouldBe(new[] { "GIT_TRACE2=0", "GIT_TRACE2_EVENT=0", "GIT_TRACE2_PERF=0" });
    }

    [Theory]
    [InlineData("https://x-access-token:ghp_abc@github.com/org/repo.git")]
    [InlineData("https://oauth2:p%40ss%2Fword@gitlab.com/org/repo.git")]
    [InlineData("http://x-access-token:t@127.0.0.1:8080/remote.git")]
    public void A_url_carrying_a_password_is_tokened(string url) => TokenedGitCommand.IsTokened(url).ShouldBeTrue();

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://user@github.com/org/repo.git")]      // a username alone is not a secret
    [InlineData("https://user:@github.com/org/repo.git")]     // nor is an empty password
    [InlineData("ssh://git@github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("file:///srv/repo.git")]
    [InlineData("/srv/repo.git")]
    public void A_url_without_a_password_is_not_tokened(string url) => TokenedGitCommand.IsTokened(url).ShouldBeFalse();

    [Fact]
    public void Every_authenticated_url_the_platform_builds_is_tokened()
    {
        TokenedGitCommand.IsTokened(LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://github.com/org/repo.git", null, "p@ss/w+rd")).ShouldBeTrue();
        TokenedGitCommand.IsTokened(LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://gitlab.com/org/repo.git", "oauth2", "glpat")).ShouldBeTrue();
        TokenedGitCommand.IsTokened(LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://github.com/org/repo.git", null, null)).ShouldBeFalse("no token: the URL is left as the operator stored it");
    }

    [Fact]
    public void A_tokened_command_gets_the_reset_ahead_of_its_arguments_and_trace2_off()
    {
        const string url = "https://x-access-token:t@host/r.git";
        var spec = new SandboxSpec { Command = "git", Args = new[] { "-c", "lfs.locksverify=false", "lfs", "push", url, "branch" }, Environment = new Dictionary<string, string> { ["LANG"] = "C", ["GIT_TRACE2"] = "/tmp/trace" }, TimeoutSeconds = 30, AllowNetwork = true };

        var tokened = TokenedGitCommand.Spec(url, spec);

        tokened.Args.ShouldBe(new[] { "-c", "credential.https://host.helper=" }.Concat(spec.Args));
        tokened.Environment["LANG"].ShouldBe("C", "the command's own environment is kept");
        foreach (var (name, value) in TokenedGitCommand.TraceOff) tokened.Environment[name].ShouldBe(value, "trace2 off wins over any value the command brought");
        (tokened with { Args = spec.Args, Environment = spec.Environment }).ShouldBe(spec, "nothing else about the command changes");
        TokenedGitSpecs.RunsTokened(tokened, "https://host").ShouldBeTrue();
    }

    [Fact]
    public void An_untokened_command_is_left_as_written()
    {
        var spec = new SandboxSpec { Command = "git", Args = new[] { "clone", "https://host/r.git", "/tmp/x" } };

        TokenedGitCommand.Spec("https://host/r.git", spec).ShouldBeSameAs(spec, "the operator's helpers may be how an untokened remote authenticates");
    }
}
