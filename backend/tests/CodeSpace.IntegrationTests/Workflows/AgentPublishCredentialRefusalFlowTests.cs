using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Failures;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the REAL <see cref="LocalGitWorkspaceProvider"/> + real <c>git</c> pushing an agent's branch to a
/// real loopback smart-HTTP remote (<see cref="GitPublishRemoteFixture"/>) that, like GitLab, revokes the access token it
/// issued the moment a new one exists and answers every request carrying the old one with a 401. Only the credential store
/// is replaced — by <see cref="ScriptedTokenSource"/>, which answers from a script so each test controls exactly which token
/// the push finds when it asks. (The real store, refresher and database are composed in
/// <see cref="AgentPublishCredentialFreshnessFlowTests"/>.)
///
/// <para>The shape is the one a long run produces: the clone is made with the token the run was provisioned with, the agent
/// works, the token is rotated away from under it, and the push — the run's last act — needs another. Each test asserts
/// from the REMOTE's side what it saw: which credentials it refused, and what landed.</para>
///
/// <para>Each test owns a GUID-named branch and temp tree, removes them on every path, and skips on Windows or without git.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentPublishCredentialRefusalFlowTests
{
    private const string Stale = "stale-access-token-aaaa";
    private const string Fresh = "fresh-access-token-bbbb";

    [Fact]
    public async Task A_push_carries_the_credential_resolved_at_the_push_not_the_one_the_clone_was_given()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new RefusalContext(tokens: new[] { Fresh });
        await ctx.StartAsync();
        await using var handle = await ctx.CloneWithAsync(Stale, repositoryNamed: true);
        ctx.RotateRemoteTokenTo(Fresh);   // the access token the clone carries is revoked while the agent works

        await ctx.AgentWritesAsync(handle.Directory);
        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        (await ctx.RemoteHasBranchAsync()).ShouldBeTrue("the branch landed on the remote");
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty("the revoked token was never presented to the remote at the push");
        ctx.TokenSource.Calls.ShouldBe(2, "the push and its readback each asked for the credential; no local command did");
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldNotBeNull("the readback was authenticated with the fresh credential too");
    }

    [Fact]
    public async Task Without_a_repository_id_the_push_carries_the_token_it_was_given_and_the_revoked_one_is_refused()
    {
        // The control: a hand-built request that names no repository keeps every earlier behaviour — the token captured at the
        // clone is the one the push carries. On a remote that revoked it, THAT is the failure a long run used to end in.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new RefusalContext(tokens: new[] { Fresh });
        await ctx.StartAsync();
        await using var handle = await ctx.CloneWithAsync(Stale, repositoryNamed: false);
        ctx.RotateRemoteTokenTo(Fresh);

        await ctx.AgentWritesAsync(handle.Directory);
        var failure = await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Message.ShouldContain("Authentication failed", Case.Insensitive, "the remote's refusal of the revoked token, as git reports it");
        failure.Message.ShouldNotContain(Stale, Case.Insensitive, "the refused token never reaches the message");
        ctx.Remote.RefusedAuthorizations.ShouldBe(new[] { GitPublishRemoteFixture.BasicAuthorization("oauth2", Stale) }, "the remote saw the revoked token, once");
        ctx.TokenSource.Calls.ShouldBe(0, "a request that names no repository never asks for a credential");
        (await ctx.RemoteHasBranchAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_push_is_tried_once_more_with_the_freshly_resolved_credential()
    {
        // The store still answered with the stale token when the push asked (a rotation another run had not stored yet), the
        // remote refused it, and the credential resolved again is a different one — which is what the second try presents.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new RefusalContext(tokens: new[] { Stale, Fresh, Fresh });
        await ctx.StartAsync();
        await using var handle = await ctx.CloneWithAsync(Stale, repositoryNamed: true);
        ctx.RotateRemoteTokenTo(Fresh);

        await ctx.AgentWritesAsync(handle.Directory);
        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        (await ctx.RemoteHasBranchAsync()).ShouldBeTrue();
        ctx.Remote.RefusedAuthorizations.ShouldBe(new[] { GitPublishRemoteFixture.BasicAuthorization("oauth2", Stale) }, "the stale credential was refused once and never presented again");
        ctx.TokenSource.Calls.ShouldBe(3, "the push, the resolve for its one retry, and the readback");
    }

    [Fact]
    public async Task A_credential_the_remote_refuses_even_after_a_fresh_resolve_is_a_typed_rejection_with_the_credentials_redacted()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new RefusalContext(tokens: new[] { Stale, "second-bad-token-cccc" });
        await ctx.StartAsync();
        await using var handle = await ctx.CloneWithAsync(Stale, repositoryNamed: true);
        ctx.RotateRemoteTokenTo(Fresh);   // neither of the two the store answers with is the live one

        await ctx.AgentWritesAsync(handle.Directory);
        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        failure.Kind.ShouldBe(FailureKind.PreconditionRequired);
        failure.Message.ShouldContain("Reconnect the provider", customMessage: "the message names what to do about it");
        failure.Message.ShouldContain(ctx.Remote.Url, customMessage: "the message names the remote, without userinfo");
        failure.Message.ShouldNotContain(Stale, Case.Insensitive);
        failure.Message.ShouldNotContain("second-bad-token-cccc", Case.Insensitive);
        ctx.Remote.RefusedAuthorizations.Count.ShouldBe(2, "each different credential was presented exactly once");
        ctx.TokenSource.Calls.ShouldBe(2, "the push and the one resolve for its retry; no readback of a push that never landed");
        (await ctx.RemoteHasBranchAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_credential_that_resolves_to_the_same_refused_one_is_not_presented_a_second_time()
    {
        // GitLab counts every failed login against the account, and a retry of the same token is refused the same way.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new RefusalContext(tokens: new[] { Stale, Stale });
        await ctx.StartAsync();
        await using var handle = await ctx.CloneWithAsync(Stale, repositoryNamed: true);
        ctx.RotateRemoteTokenTo(Fresh);

        await ctx.AgentWritesAsync(handle.Directory);
        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Message.ShouldContain("returned the same one");
        ctx.Remote.RefusedAuthorizations.ShouldBe(new[] { GitPublishRemoteFixture.BasicAuthorization("oauth2", Stale) }, "the refused token went out once, not once per attempt");
        ctx.TokenSource.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task What_real_git_prints_for_a_refused_credential_is_what_the_classifier_reads()
    {
        // The fixture check for every scripted refusal in the unit tests: git's own words, from git, against a remote that
        // answered 401 — never a string typed from memory.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new RefusalContext(tokens: Array.Empty<string>());
        await ctx.StartAsync();
        await using var handle = await ctx.CloneWithAsync(Stale, repositoryNamed: false);
        ctx.RotateRemoteTokenTo(Fresh);

        await ctx.AgentWritesAsync(handle.Directory);
        var failure = await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        var stderr = failure.Message[(failure.Message.IndexOf("): ", StringComparison.Ordinal) + 3)..];
        Console.WriteLine($"[credential-refusal] git said: {stderr}");

        LocalGitWorkspaceProvider.IsCredentialRefusal(stderr).ShouldBeTrue($"git's refusal text was not recognised: {stderr}");
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>The credential store, from a script: answers each ask with the next token (repeating the last), and counts the asks.</summary>
    private sealed class ScriptedTokenSource(IReadOnlyList<string> tokens) : IWorkspaceTokenSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<string?> ResolveAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;

            return Task.FromResult<string?>(tokens.Count == 0 ? null : tokens[Math.Min(index, tokens.Count - 1)]);
        }
    }

    /// <summary>A hermetic git environment: no operator config, no prompt, a HOME of its own — the runner under test otherwise inherits the host's.</summary>
    private sealed class HermeticRunner(string home) : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();

        public string Kind => "local";

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            var environment = new Dictionary<string, string>(spec.Environment) { ["HOME"] = home, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig"), ["GIT_TERMINAL_PROMPT"] = "0" };

            return _inner.RunAsync(spec with { Environment = environment }, cancellationToken);
        }
    }

    private sealed class RefusalContext : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-credrefusal-" + Guid.NewGuid().ToString("N"));
        private readonly Guid _repositoryId = Guid.NewGuid();
        private readonly Guid _teamId = Guid.NewGuid();

        public RefusalContext(IReadOnlyList<string> tokens)
        {
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            TokenSource = new ScriptedTokenSource(tokens);
            Provider = new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new ISandboxRunner[] { new HermeticRunner(Path.Combine(_root, "home")) }), TokenSource, NullLogger<LocalGitWorkspaceProvider>.Instance, Path.Combine(_root, "workspaces"));
        }

        /// <summary>Reads authenticate too, as a private GitLab project's do, so the clone itself proves the token it was given.</summary>
        public GitPublishRemoteFixture Remote { get; } = new() { AuthenticateReads = true };
        public ScriptedTokenSource TokenSource { get; }
        public LocalGitWorkspaceProvider Provider { get; }
        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        public async Task StartAsync()
        {
            await Remote.StartAsync();
            Remote.AcceptOnly("oauth2", Stale);   // the token the run is provisioned with is the live one — until it is rotated
        }

        /// <summary>The provider issues a new access token and revokes the old: from here the remote refuses the token the clone carries.</summary>
        public void RotateRemoteTokenTo(string token) => Remote.AcceptOnly("oauth2", token);

        public async Task<IWorkspaceHandle> CloneWithAsync(string token, bool repositoryNamed) =>
            await Provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest
            {
                RepositoryUrl = Remote.Url, Token = token, TokenUsername = "oauth2", Depth = 1,
                RepositoryId = repositoryNamed ? _repositoryId : null, TeamId = repositoryNamed ? _teamId : null,
            }), CancellationToken.None);

        public Task AgentWritesAsync(string cloneDir) => File.WriteAllTextAsync(Path.Combine(cloneDir, "agent.txt"), "the agent's work\n");

        public async Task<bool> RemoteHasBranchAsync() =>
            !string.IsNullOrWhiteSpace(await GitPublishRemoteFixture.GitAsync(_root, new[] { "--git-dir", Remote.Remote, "for-each-ref", $"refs/heads/{BranchName}" }));

        public void Dispose()
        {
            Remote.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
