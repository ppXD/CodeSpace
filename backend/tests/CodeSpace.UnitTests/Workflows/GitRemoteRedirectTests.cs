using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A tokened git command whose remote redirects to another origin. git follows the redirect itself and then asks for a
/// credential at the origin it was sent to; the credential helper answers for the remote's own origin only, so none is found
/// and git says <c>could not read Username for '&lt;that origin&gt;': terminal prompts disabled</c> — git's own text, which
/// reads like a credential that was never configured. The failure names the redirect instead (the typed
/// <c>provider_base_url_redirects</c> and the origin git was sent to), as a provider's REST call to the same address does,
/// and stays a <see cref="WorkspaceException"/> so every catch site that treats a git failure as one still does. A prompt at
/// the remote's OWN origin is not a redirect — a remote that refused the credential once is not asked again — and is left as
/// the plain failure it was.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitRemoteRedirectTests
{
    private const string Remote = "http://127.0.0.1:18102/g/r.git";

    [Theory]
    [InlineData("fatal: could not read Username for 'https://127.0.0.1:18443': terminal prompts disabled", "https://127.0.0.1:18443")]                              // http to https
    [InlineData("fatal: could not read Username for 'http://localhost:18102': terminal prompts disabled", "http://localhost:18102")]                             // a host alias
    [InlineData("fatal: could not read Username for 'http://127.0.0.1:18103': No such device or address", "http://127.0.0.1:18103")]                             // another port, git without a terminal
    [InlineData("remote: HTTP Basic: Access denied\nfatal: could not read Password for 'http://svc@other.test': terminal prompts disabled", "http://other.test")]  // userinfo is not part of an origin
    [InlineData("fatal: could not read Username for 'http://OTHER.test:80': terminal prompts disabled", "http://other.test")]                                    // a scheme's own port is not spelled
    public void The_origin_git_was_sent_to_is_named_when_it_is_not_the_remotes_own(string stderr, string expectedOrigin)
    {
        var redirect = GitRemoteRedirect.Named(Remote, stderr).ShouldNotBeNull();

        redirect.FinalOrigin.ShouldBe(expectedOrigin);
        redirect.RequestedOrigin.ShouldBe("http://127.0.0.1:18102");
        redirect.Message.ShouldContain(expectedOrigin);
        redirect.Message.ShouldNotContain("svc", Case.Sensitive, "a userinfo git echoed is not repeated");
    }

    [Theory]
    [InlineData("fatal: could not read Username for 'http://127.0.0.1:18102': terminal prompts disabled")]                  // the remote's own origin: it refused the credential, not a redirect
    [InlineData("fatal: could not read Username for 'HTTP://127.0.0.1:18102': terminal prompts disabled")]                  // another spelling of it
    [InlineData("fatal: repository 'http://127.0.0.1:18102/g/r.git/' not found")]                                           // some other failure
    [InlineData("fatal: could not read Username for 'not a url': terminal prompts disabled")]                               // nothing parseable to name
    [InlineData("fatal: could not read Username for 'ssh://git@host/g/r.git': terminal prompts disabled")]                  // not an http(s) origin
    [InlineData("")]
    public void Nothing_is_named_when_git_did_not_leave_the_remotes_origin(string stderr) =>
        GitRemoteRedirect.Named(Remote, stderr).ShouldBeNull();

    [Theory]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("file:///srv/git/repo.git")]
    public void A_remote_that_is_not_http_has_no_origin_to_have_left(string remote) =>
        GitRemoteRedirect.Named(remote, "fatal: could not read Username for 'https://elsewhere.test': terminal prompts disabled").ShouldBeNull();

    [Fact]
    public void The_failure_is_still_a_workspace_exception_that_carries_the_redirect_as_its_code_and_details()
    {
        var failure = GitRemoteRedirect.Failure(Remote, "fatal: could not read Username for 'https://127.0.0.1:18443': terminal prompts disabled", "git clone failed (exit 128): fatal: could not read Username");

        failure.Message.ShouldStartWith("git clone failed (exit 128)");
        failure.Message.ShouldContain("https://127.0.0.1:18443");
        failure.Message.ShouldContain("Edit the provider's base URL");
        failure.InnerException.ShouldBeOfType<ProviderBaseUrlRedirectsException>();
        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        ((IFailure)failure).Kind.ShouldBe(FailureKind.Unprocessable);
        ((IFailure)failure).Details!["finalOrigin"].ShouldBe("https://127.0.0.1:18443");
    }

    [Fact]
    public void Any_other_failure_keeps_its_own_message_and_code()
    {
        var failure = GitRemoteRedirect.Failure(Remote, "fatal: could not read Username for 'http://127.0.0.1:18102': terminal prompts disabled", "git clone failed (exit 128): the plain failure");

        failure.Message.ShouldBe("git clone failed (exit 128): the plain failure");
        failure.InnerException.ShouldBeNull();
        ((IFailure)failure).Code.ShouldBe("workspace_unresolvable");
        ((IFailure)failure).Details.ShouldBeNull();
    }

    [Fact]
    public async Task The_launch_base_probe_names_the_redirect_git_met()
    {
        var runner = new FailingRunner("fatal: could not read Username for 'https://127.0.0.1:18443': terminal prompts disabled");

        var failure = await Should.ThrowAsync<WorkspaceException>(() => new RemoteTipResolver(new SandboxRunnerRegistry(new[] { runner })).ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = Remote, Token = "t0ken", TokenUsername = "oauth2" }, refRequired: true, CancellationToken.None));

        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        failure.Message.ShouldContain("git ls-remote failed");
        failure.Message.ShouldContain("https://127.0.0.1:18443");
        failure.Message.ShouldNotContain("t0ken", Case.Sensitive);
    }

    [Fact]
    public async Task The_launch_base_probe_leaves_a_prompt_at_the_remotes_own_origin_as_the_plain_failure()
    {
        var runner = new FailingRunner("fatal: could not read Username for 'http://127.0.0.1:18102': terminal prompts disabled");

        var failure = await Should.ThrowAsync<WorkspaceException>(() => new RemoteTipResolver(new SandboxRunnerRegistry(new[] { runner })).ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = Remote, Token = "t0ken", TokenUsername = "oauth2" }, refRequired: true, CancellationToken.None));

        ((IFailure)failure).Code.ShouldBe("workspace_unresolvable");
        failure.Message.ShouldContain("git ls-remote failed");
    }

    [Fact]
    public async Task The_workspace_clone_names_the_redirect_git_met()
    {
        var runner = new FailingRunner("fatal: could not read Username for 'http://localhost:18102': terminal prompts disabled", failingArg: "clone");
        var provider = new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new[] { runner }), NullLogger<LocalGitWorkspaceProvider>.Instance);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = Remote, Token = "t0ken", TokenUsername = "oauth2" }), CancellationToken.None));

        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        failure.Message.ShouldContain("git clone failed");
        ((IFailure)failure).Details!["finalOrigin"].ShouldBe("http://localhost:18102");
        runner.Invocations.ShouldContain(args => args.Contains("clone"), "fixture check: the clone ran and is what failed");
    }

    /// <summary>A runner on which every git command succeeds with no output — except the ones naming <c>failingArg</c> (every one when null), which fail with <c>stderr</c>.</summary>
    private sealed class FailingRunner(string stderr, string? failingArg = null) : ISandboxRunner
    {
        public string Kind => SandboxKinds.Local;

        public List<IReadOnlyList<string>> Invocations { get; } = new();

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Invocations.Add(spec.Args);

            if (spec.WorkingDirectory is { Length: > 0 } directory) Directory.CreateDirectory(directory);

            var fails = failingArg is null || spec.Args.Contains(failingArg);

            return Task.FromResult(fails
                ? new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 128, Stdout = "", Stderr = stderr }
                : new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });
        }
    }
}
