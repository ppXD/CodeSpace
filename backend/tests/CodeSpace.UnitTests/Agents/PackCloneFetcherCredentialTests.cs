using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// A pasted pack URL can carry a credential in its userinfo: as the password (<c>x-access-token:&lt;token&gt;@</c>,
/// <c>oauth2:&lt;token&gt;@</c>) or as the user (<c>&lt;token&gt;@</c>, <c>&lt;token&gt;:x-oauth-basic@</c>). Pins that a clone
/// failure names the URL without it and redacts it from git's stderr in every spelling git echoes it in, while a user named
/// beside a password leaves git's reason readable; that the clone names the remote without the credential, in an
/// owner-only directory, and still points origin at that URL once cloned as a belt (the clone refused when neither rewrite
/// nor removal works); and that a URL with no credential, an ssh URL's <c>git@</c> included, is left as written.
/// <c>PackCloneCredentialFlowTests</c> proves
/// the same against real git and a remote that demands the token.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PackCloneFetcherCredentialTests
{
    /// <summary>In every fake credential below, in every spelling, so one absence check covers them all.</summary>
    private const string Marker = "token-0123456789";

    private const string Tokened = "https://x-access-token:fake-pasted-token-0123456789@github.com/owner/repo.git";

    [Theory]
    [InlineData("https://x-access-token:fake-pasted-token-0123456789@github.com/owner/repo.git", "fatal: Authentication failed for 'https://github.com/owner/repo.git/'")]
    [InlineData("https://oauth2:fake-pasted-token-0123456789@github.com/owner/repo.git", "fatal: Authentication failed for 'https://github.com/owner/repo.git/'")]
    [InlineData("https://fake-pasted-token-0123456789@github.com/owner/repo.git", "fatal: could not read Password for 'https://fake-pasted-token-0123456789@github.com': terminal prompts disabled")]
    [InlineData("https://fake-pasted-token-0123456789:x-oauth-basic@github.com/owner/repo.git", "fatal: unable to access 'https://fake-pasted-token-0123456789:x-oauth-basic@github.com/owner/repo.git/': error: 403")]
    public void A_clone_failure_names_the_url_without_its_credential(string url, string stderr)
    {
        var message = PackCloneFetcher.CloneFailedMessage(url, Failed(stderr));

        message.ShouldNotContain(Marker, Case.Sensitive, "the message reaches the API error body, the UI and the mediator's error log");
        message.ShouldStartWith("git clone of 'https://github.com/owner/repo.git' failed (Failed, exit 128): ", Case.Sensitive, "the URL is still named, without its userinfo");
    }

    [Theory]
    [InlineData("fake/pasted@token-0123456789")]       // git 2.33 echoes the user decoded
    [InlineData("fake%2Fpasted%40token-0123456789")]   // later git re-encodes it
    [InlineData("fake%2fpasted%40token-0123456789")]   // as pasted
    public void A_percent_encoded_credential_is_redacted_in_every_spelling_git_echoes(string echoed)
    {
        const string url = "https://fake%2fpasted%40token-0123456789@gitlab.com/group/pack.git";

        var message = PackCloneFetcher.CloneFailedMessage(url, Failed($"fatal: could not read Password for 'https://{echoed}@gitlab.com': terminal prompts disabled"));

        message.ShouldNotContain(Marker);
        message.ShouldEndWith("fatal: could not read Password for 'https://***@gitlab.com': terminal prompts disabled", Case.Sensitive, "git's reason stays readable around the redaction");
    }

    [Theory]
    [InlineData("fake%2fpasted%40token-0123456789")]   // as pasted
    [InlineData("fake/pasted@token-0123456789")]       // decoded
    [InlineData("fake%2Fpasted%40token-0123456789")]   // re-encoded
    public void A_password_echoed_outside_a_url_is_redacted_in_every_spelling(string echoed)
    {
        // git keeps a password out of the URLs it names, but a remote can echo the token it was handed.
        const string url = "https://x-access-token:fake%2fpasted%40token-0123456789@github.com/owner/repo.git";

        var message = PackCloneFetcher.CloneFailedMessage(url, Failed($"remote: Invalid token {echoed}. fatal: Authentication failed for 'https://github.com/owner/repo.git/'"));

        message.ShouldNotContain(Marker);
        message.ShouldEndWith("remote: Invalid token ***. fatal: Authentication failed for 'https://github.com/owner/repo.git/'", Case.Sensitive);
    }

    [Theory]
    [InlineData("https://a:fake-pasted-token-0123456789@gitlab.com/group/pack.git", "fatal: Authentication failed for 'https://gitlab.com/group/pack.git/'")]
    [InlineData("https://owner:fake-pasted-token-0123456789@github.com/owner/repo.git", "fatal: repository 'https://github.com/owner/repo.git/' not found")]
    public void A_user_named_beside_a_password_leaves_gits_reason_readable(string url, string stderr)
    {
        // The password carries the credential; the user beside it names an account, and git names it only inside a URL.
        // Redacting it as bare text would mask every 'a' in the reason, or the owner in the repository's path.
        PackCloneFetcher.CloneFailedMessage(url, Failed(stderr)).ShouldEndWith($"): {stderr}", Case.Sensitive);
    }

    [Fact]
    public async Task The_clone_directory_is_owner_only_before_git_runs_in_it()
    {
        // A belt: the clone names the remote without the pasted credential, so git writes none into .git/config, but the
        // checkout is still the import's private copy until it is walked.
        if (OperatingSystem.IsWindows()) return;

        var runner = new ScriptedRunner();

        using var checkout = await Fetcher(runner).FetchAsync(Tokened, null, CancellationToken.None);

        runner.CloneDirectoryMode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "no other uid on the host can read the clone while git writes it");
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "fatal: repository 'https://github.com/owner/repo.git/' not found")]
    [InlineData("ssh://git@github.com/owner/repo.git", "git@github.com: Permission denied (publickey).")]
    [InlineData("git@github.com:owner/repo.git", "git@github.com: Permission denied (publickey).")]
    public void A_url_without_a_credential_is_named_as_written(string url, string stderr)
    {
        // Positive control for the redaction: an ssh URL's user is an account, never a token, so neither it nor git's
        // stderr naming it is touched.
        PackCloneFetcher.CloneFailedMessage(url, Failed(stderr)).ShouldBe($"git clone of '{url}' failed (Failed, exit 128): {stderr}");
    }

    [Fact]
    public async Task A_failed_clone_throws_without_the_credential_and_leaves_no_clone()
    {
        var runner = new ScriptedRunner { CloneStderr = "fatal: could not read Password for 'https://fake-pasted-token-0123456789@github.com': terminal prompts disabled" };

        var failure = await Should.ThrowAsync<PackImportException>(() => Fetcher(runner).FetchAsync("https://fake-pasted-token-0123456789@github.com/owner/repo.git", null, CancellationToken.None));

        failure.Message.ShouldNotContain(Marker);
        Directory.Exists(runner.Specs.Single().WorkingDirectory).ShouldBeFalse("a failed clone is reclaimed before the throw");
    }

    [Theory]
    [InlineData(Tokened)]
    [InlineData("https://fake-pasted-token-0123456789@github.com/owner/repo.git")]   // a token pasted as the user alone
    public async Task A_pasted_credential_never_reaches_origin_and_the_strip_stays_as_a_belt(string url)
    {
        var runner = new ScriptedRunner();

        using var checkout = await Fetcher(runner).FetchAsync(url, null, CancellationToken.None);

        runner.Specs.Count.ShouldBe(2, "the clone, then one rewrite of origin");
        runner.Specs[0].Args.ShouldContain("https://github.com/owner/repo.git", "the clone names the remote without the pasted credential, so origin never holds it");
        runner.Specs[0].Args.ShouldNotContain(a => a.Contains(Marker), "no argv carries the pasted credential");
        runner.Specs[1].Args.ShouldBe(new[] { "-C", checkout.Directory, "remote", "set-url", "origin", "https://github.com/owner/repo.git" }, "the strip sets origin to the URL it already holds");
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    public async Task A_url_without_a_credential_is_cloned_with_origin_as_written(string url)
    {
        var runner = new ScriptedRunner();

        using var checkout = await Fetcher(runner).FetchAsync(url, null, CancellationToken.None);

        runner.Specs.Count.ShouldBe(1, "nothing to strip, so no rewrite runs");
    }

    [Fact]
    public async Task A_clone_that_cannot_shed_the_credential_is_refused_and_reclaimed()
    {
        var runner = new ScriptedRunner { RemoteEditsFail = true };

        var failure = await Should.ThrowAsync<WorkspaceException>(() => Fetcher(runner).FetchAsync(Tokened, null, CancellationToken.None));

        failure.Message.ShouldContain(LocalGitWorkspaceProvider.TokenStripFailedDetail, Case.Sensitive, "the workspace provider's own fail-closed strip");
        runner.Specs.Count.ShouldBe(3, "the clone, the rewrite, then the removal");
        Directory.Exists(runner.Specs[0].WorkingDirectory).ShouldBeFalse("a clone still holding the credential is deleted before the throw");
    }

    private static PackCloneFetcher Fetcher(ScriptedRunner runner) =>
        new(new AllowAll(), new SandboxRunnerRegistry(new ISandboxRunner[] { runner }), NullLogger<PackCloneFetcher>.Instance);

    private static SandboxResult Failed(string stderr) => new() { Status = SandboxStatus.Failed, ExitCode = 128, Stdout = "", Stderr = stderr };

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }

    /// <summary>Records every spec, and the clone directory's mode as git would find it; the clone succeeds unless <see cref="CloneStderr"/> is set, and the <c>git remote</c> edits succeed unless <see cref="RemoteEditsFail"/>.</summary>
    private sealed class ScriptedRunner : ISandboxRunner
    {
        public string Kind => "local";
        public string? CloneStderr { get; init; }
        public bool RemoteEditsFail { get; init; }
        public List<SandboxSpec> Specs { get; } = new();
        public UnixFileMode? CloneDirectoryMode { get; private set; }

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            if (Specs.Count == 0 && !OperatingSystem.IsWindows()) CloneDirectoryMode = File.GetUnixFileMode(spec.WorkingDirectory!);

            Specs.Add(spec);

            var fails = spec.Args.Contains("remote") ? RemoteEditsFail : CloneStderr is not null;

            return Task.FromResult(fails ? Failed(CloneStderr ?? "error: could not lock config file") : new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });
        }
    }
}
