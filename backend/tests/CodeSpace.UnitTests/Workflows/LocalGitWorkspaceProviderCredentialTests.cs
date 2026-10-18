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
/// The credential a <see cref="LocalGitWorkspaceProvider"/> pushes with is resolved when the push runs, not when the clone was
/// made: a run outlives the access token it was provisioned with, and a GitLab access token is revoked the moment any caller
/// refreshes it. These tests pin the provider's side of that against a SCRIPTED runner (no git, no network, no database): which
/// commands ask the <see cref="IWorkspaceTokenSource"/>, which token each carries, what a refusal does — one retry with a
/// DIFFERENT credential, never the same one twice — and that every credential it ever held stays out of what it throws.
///
/// <para>The refusal text the script answers with is git's own, captured from git 2.33 against a remote that answered 401 by
/// <c>AgentPublishCredentialRefusalFlowTests.What_real_git_prints_for_a_refused_credential_is_what_the_classifier_reads</c>
/// — the integration test that fails if git's wording ever stops matching, so this fixture cannot drift from production
/// unseen. The real chain (resolver, refresher, database, git, a remote that revokes) is composed in the integration tests.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalGitWorkspaceProviderCredentialTests
{
    private const string Url = "https://example.test/repo.git";
    private const string Branch = "codespace/run";

    /// <summary>What git 2.33 wrote to stderr when the remote answered a push with 401 after it was offered a credential.</summary>
    private const string GitRefusal = "fatal: Authentication failed for 'https://example.test/repo.git/'";

    // ─── Which commands ask, and what each carries ───────────────────────────────────

    [Fact]
    public async Task Every_command_that_reaches_the_remote_asks_the_token_source_first_and_carries_what_it_answers_and_no_local_command_asks()
    {
        var source = new ScriptedSource("push-token", "readback-token");
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        await publish.PushAsync();

        var remoteSpecs = publish.Runner.RemoteSpecs;
        remoteSpecs.Select(Subcommand).ShouldBe(new[] { "push", "ls-remote" }, "fixture check: the push and its readback are the commands that reach the remote");
        TokenedGitSpecs.CarriesTheCredential(remoteSpecs[0], "oauth2", "push-token").ShouldBeTrue("the push carries the credential resolved for it");
        TokenedGitSpecs.CarriesTheCredential(remoteSpecs[1], "oauth2", "readback-token").ShouldBeTrue("the readback resolves its own — the push may have run for minutes");
        source.Asked.ShouldBe(2, "one ask per remote command; the bundles, the checkout, the commit and the imports reach no remote and never ask");
        publish.Runner.PostCloneSpecs.Where(s => TokenedGitSpecs.CarriesTheCredential(s, "oauth2", "clone-token")).ShouldBeEmpty("the token the clone was provisioned with is never presented once the source answers");
    }

    [Fact]
    public async Task The_lfs_upload_the_push_and_the_readback_each_ask_in_that_order()
    {
        var source = new ScriptedSource("lfs-token", "push-token", "readback-token");
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);
        publish.PlantALfsObject();

        await publish.PushAsync();

        publish.Runner.RemoteSpecs.Select(Subcommand).ShouldBe(new[] { "lfs", "push", "ls-remote" });
        publish.Runner.RemoteSpecs.Select(s => s.Environment["CODESPACE_GIT_PASSWORD"]).ShouldBe(new[] { "lfs-token", "push-token", "readback-token" }, "each command carries the credential resolved immediately before it");
        source.Asked.ShouldBe(3);
    }

    [Fact]
    public async Task A_second_push_of_the_same_workspace_resolves_its_credential_again()
    {
        // An S6 revise round re-pushes the same branch from the same workspace, possibly an hour later.
        var source = new ScriptedSource("first-push", "first-readback", "second-push", "second-readback");
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        await publish.PushAsync();
        await publish.PushAsync();

        publish.Runner.RemoteSpecs.Select(s => s.Environment["CODESPACE_GIT_PASSWORD"]).ShouldBe(new[] { "first-push", "first-readback", "second-push", "second-readback" });
    }

    [Fact]
    public async Task A_request_that_names_no_repository_keeps_the_token_it_was_given_and_never_asks()
    {
        var source = new ScriptedSource("never-used");
        await using var publish = await PrepareAsync(new ScriptedRunner(), source, repositoryNamed: false);

        await publish.PushAsync();

        publish.Runner.RemoteSpecs.ShouldAllBe(s => TokenedGitSpecs.CarriesTheCredential(s, "oauth2", "clone-token"));
        source.Asked.ShouldBe(0, "byte-identical for every request built without the resolver");
    }

    [Fact]
    public async Task A_provider_with_no_token_source_keeps_the_token_it_was_given()
    {
        await using var publish = await PrepareAsync(new ScriptedRunner(), source: null, repositoryNamed: true);

        await publish.PushAsync();

        publish.Runner.RemoteSpecs.ShouldAllBe(s => TokenedGitSpecs.CarriesTheCredential(s, "oauth2", "clone-token"));
    }

    [Fact]
    public async Task An_anonymous_clone_still_publishes_nothing_and_never_asks()
    {
        var source = new ScriptedSource("never-used");
        await using var publish = await PrepareAsync(new ScriptedRunner(), source, cloneToken: null);

        (await publish.PushAsync()).ShouldBeNull("a clone with no credential has nothing to push with — no failure, no branch");

        publish.Runner.RemoteSpecs.ShouldBeEmpty();
        source.Asked.ShouldBe(0);
    }

    // ─── A refusal ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refused_push_is_tried_once_more_with_the_credential_resolved_again_when_it_differs()
    {
        var source = new ScriptedSource("stale-token", "fresh-token", "readback-token");
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "push" && spec.Environment["CODESPACE_GIT_PASSWORD"] == "stale-token" ? Refusal() : null };
        await using var publish = await PrepareAsync(runner, source);

        (await publish.PushAsync()).ShouldBe(Branch);

        runner.RemoteSpecs.Select(Subcommand).ShouldBe(new[] { "push", "push", "ls-remote" });
        runner.RemoteSpecs.Select(s => s.Environment["CODESPACE_GIT_PASSWORD"]).ShouldBe(new[] { "stale-token", "fresh-token", "readback-token" });
        source.Asked.ShouldBe(3, "the push, the resolve for its one retry, and the readback");
    }

    [Fact]
    public async Task A_refused_push_whose_credential_resolves_to_the_same_one_is_not_presented_again_and_is_typed()
    {
        var source = new ScriptedSource("dead-token");
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "push" ? Refusal() : null };
        await using var publish = await PrepareAsync(runner, source);

        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => publish.PushAsync());

        runner.RemoteSpecs.Count(s => Subcommand(s) == "push").ShouldBe(1, "the same refused token is never put to the remote a second time");
        failure.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        failure.Kind.ShouldBe(FailureKind.PreconditionRequired);
        failure.Message.ShouldContain(Url, customMessage: "the message names the remote, without userinfo");
        failure.Message.ShouldContain("returned the same one");
        failure.Message.ShouldContain("Reconnect the provider");
        failure.Message.ShouldNotContain("dead-token", Case.Insensitive);
        runner.RemoteSpecs.ShouldNotContain(s => Subcommand(s) == "ls-remote", "a push that never landed has nothing to read back");
    }

    [Fact]
    public async Task A_push_refused_with_two_different_credentials_is_typed_after_exactly_two_tries()
    {
        var source = new ScriptedSource("first-bad", "second-bad");
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "push" ? Refusal() : null };
        await using var publish = await PrepareAsync(runner, source);

        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => publish.PushAsync());

        runner.RemoteSpecs.Count(s => Subcommand(s) == "push").ShouldBe(2);
        failure.Message.ShouldContain("again after resolving a fresh one");
        failure.Message.ShouldNotContain("first-bad", Case.Insensitive);
        failure.Message.ShouldNotContain("second-bad", Case.Insensitive);
    }

    [Fact]
    public async Task A_refused_lfs_upload_gets_the_same_one_fresh_try_before_the_push_is_attempted()
    {
        var source = new ScriptedSource("stale-token", "fresh-token", "push-token", "readback-token");
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "lfs" && spec.Environment["CODESPACE_GIT_PASSWORD"] == "stale-token" ? Refusal("refused this credential") : null };
        await using var publish = await PrepareAsync(runner, source);
        publish.PlantALfsObject();

        (await publish.PushAsync()).ShouldBe(Branch);

        runner.RemoteSpecs.Select(Subcommand).ShouldBe(new[] { "lfs", "lfs", "push", "ls-remote" });
    }

    [Fact]
    public async Task A_remote_that_refuses_the_action_rather_than_the_credential_stays_the_plain_failure_and_is_not_resolved_again()
    {
        // A 403: the credential was accepted and the action refused (a read-only token, a protected branch). No other token
        // for the same account changes that, and the push retry above this decides what to do with a plain failure.
        var source = new ScriptedSource("a-token");
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "push" ? new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 128, Stdout = "", Stderr = "remote: You are not allowed to push code to this project.\nfatal: unable to access 'https://example.test/repo.git/': The requested URL returned error: 403" } : null };
        await using var publish = await PrepareAsync(runner, source);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => publish.PushAsync());

        failure.Message.ShouldContain("403");
        runner.RemoteSpecs.Count(s => Subcommand(s) == "push").ShouldBe(1);
        source.Asked.ShouldBe(1, "no second resolve for a failure that is not a refusal of the credential");
    }

    [Fact]
    public async Task A_refusal_on_a_request_that_names_no_repository_stays_the_plain_failure()
    {
        // Nothing to resolve again, so nothing to retry and nothing new to say: the caller sees what it always saw.
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "push" ? Refusal() : null };
        await using var publish = await PrepareAsync(runner, new ScriptedSource("never-used"), repositoryNamed: false);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => publish.PushAsync());

        failure.Message.ShouldContain("Authentication failed");
        runner.RemoteSpecs.Count(s => Subcommand(s) == "push").ShouldBe(1);
    }

    [Fact]
    public async Task Every_credential_the_repository_carried_is_redacted_from_what_the_provider_throws()
    {
        // The remote can echo a credential back; both the one that was refused and the one resolved for the retry are secrets.
        var source = new ScriptedSource("stale-secret-111", "fresh-secret-222");
        var runner = new ScriptedRunner { Remote = (spec, _) => Subcommand(spec) == "push" ? new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 128, Stdout = "", Stderr = $"remote: HTTP Basic: Access denied. The token was {spec.Environment["CODESPACE_GIT_PASSWORD"]}\n{GitRefusal}" } : null };
        await using var publish = await PrepareAsync(runner, source);

        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => publish.PushAsync());

        failure.Message.ShouldNotContain("stale-secret-111", Case.Insensitive);
        failure.Message.ShouldNotContain("fresh-secret-222", Case.Insensitive);
        failure.Message.ShouldContain("***", customMessage: "the echo is redacted, not dropped");
    }

    // ─── When the credential cannot be resolved ──────────────────────────────────────

    [Fact]
    public async Task A_credential_disconnected_while_the_run_worked_is_typed_and_nothing_reaches_the_remote()
    {
        var source = new ScriptedSource { Answer = _ => throw new CredentialDisconnectedException(Guid.NewGuid(), "GitLab · someone") };
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => publish.PushAsync());

        failure.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        failure.Message.ShouldContain("was disconnected");
        failure.InnerException.ShouldBeOfType<CredentialDisconnectedException>();
        publish.Runner.RemoteSpecs.ShouldBeEmpty("nothing was put to the remote");
    }

    [Fact]
    public async Task A_failure_to_resolve_the_credential_is_a_plain_workspace_failure_the_push_retry_can_retry()
    {
        // A store hiccup, a provider that did not answer: transient until proven otherwise, and the retry above the provider
        // only retries a WorkspaceException — an escaping raw exception would flip the run that produced the work.
        var source = new ScriptedSource { Answer = _ => throw new TimeoutException("the credential store did not answer") };
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => publish.PushAsync());

        failure.Message.ShouldContain("Could not resolve the git credential to push with");
        failure.Message.ShouldContain("the credential store did not answer");
        failure.InnerException.ShouldBeOfType<TimeoutException>();
        publish.Runner.RemoteSpecs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_repository_with_no_credential_any_more_has_nothing_to_push_with()
    {
        var source = new ScriptedSource { Answer = _ => null };
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => publish.PushAsync());

        failure.Message.ShouldContain("no credential bound any more");
        publish.Runner.RemoteSpecs.ShouldBeEmpty("an anonymous push is never attempted for a repository that was provisioned with a credential");
    }

    [Fact]
    public async Task The_runs_own_cancellation_while_resolving_the_credential_propagates_unwrapped()
    {
        using var cancelled = new CancellationTokenSource();
        var source = new ScriptedSource("never-answered");
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => publish.PushAsync(cancelled.Token));
        publish.Runner.RemoteSpecs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_provider_call_that_timed_out_while_resolving_the_credential_is_a_retryable_workspace_failure_not_a_cancelled_run()
    {
        // An HTTP timeout inside the OAuth refresh surfaces as a TaskCanceledException while the run's own token is untouched:
        // letting it through would end the run as cancelled instead of recording a failed publish.
        var source = new ScriptedSource { Answer = _ => throw new TaskCanceledException("the token endpoint timed out", new TimeoutException()) };
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => publish.PushAsync());

        failure.Message.ShouldContain("Could not resolve the git credential to push with");
        failure.InnerException.ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task Each_repository_of_a_multi_repo_workspace_asks_for_its_own_credential()
    {
        var (webId, apiId, teamId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var source = new ScriptedSource { Answer = _ => "any-token" };
        var asked = new List<(Guid RepositoryId, Guid TeamId)>();
        source.OnAsk = (repositoryId, team) => asked.Add((repositoryId, team));
        var runner = new ScriptedRunner();
        var provider = new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new[] { runner }), source, NullLogger<LocalGitWorkspaceProvider>.Instance, Path.Combine(Path.GetTempPath(), "cs-credunit-" + Guid.NewGuid().ToString("N")));

        await using var handle = await provider.PrepareAsync(new WorkspaceProvisionRequest
        {
            PrimaryAlias = "web",
            Repositories = new[]
            {
                new WorkspaceRepositoryProvision { Alias = "web", CloneRequest = new WorkspaceRequest { RepositoryUrl = "https://example.test/web.git", Token = "web-clone", TokenUsername = "oauth2", RepositoryId = webId, TeamId = teamId }, Access = WorkspaceAccess.Write, IsPrimary = true },
                new WorkspaceRepositoryProvision { Alias = "api", CloneRequest = new WorkspaceRequest { RepositoryUrl = "https://example.test/api.git", Token = "api-clone", TokenUsername = "oauth2", RepositoryId = apiId, TeamId = teamId }, Access = WorkspaceAccess.Write },
            },
        }, CancellationToken.None);

        await ((IWorkspacePushHandle)handle).PushChangesAsync("web", Branch, CancellationToken.None);
        await ((IWorkspacePushHandle)handle).PushChangesAsync("api", Branch, CancellationToken.None);

        asked.Select(a => $"{a.RepositoryId}/{a.TeamId}").ShouldBe(new[] { $"{webId}/{teamId}", $"{webId}/{teamId}", $"{apiId}/{teamId}", $"{apiId}/{teamId}" }, customMessage: "each repo's push and readback ask for the repository they were resolved for, never a sibling's");
    }

    [Fact]
    public async Task A_readback_whose_credential_cannot_be_resolved_withholds_the_confirmation_but_keeps_the_push()
    {
        // The push already landed. A credential that cannot be resolved a moment later costs only the confirmation.
        var source = new ScriptedSource { Answer = ask => ask == 1 ? "push-token" : throw new TimeoutException("the credential store did not answer") };
        await using var publish = await PrepareAsync(new ScriptedRunner(), source);

        (await publish.PushAsync()).ShouldBe(Branch, "the branch was pushed");
        publish.LastPushedCommitSha().ShouldBeNull("arrival stays unconfirmed — never fabricated");
        publish.Runner.RemoteSpecs.Select(Subcommand).ShouldBe(new[] { "push" }, "the readback never ran without a credential");
    }

    // ─── Classifying a refusal ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://example.test/repo.git/'", true)]                               // git, after a 401 on a credential it offered (captured from git 2.33)
    [InlineData("remote: HTTP Basic: Access denied", true)]                                                             // the body GitLab sends with the 401
    [InlineData("fatal: unable to access 'https://example.test/repo.git/': The requested URL returned error: 401", true)] // curl's own words
    [InlineData("the remote refused this credential; not offering it again", true)]                                       // our credential helper, what git-lfs reads after the 401
    [InlineData("FATAL: AUTHENTICATION FAILED FOR 'https://example.test/'", true)]                                       // case-insensitive
    [InlineData("fatal: unable to access 'https://example.test/repo.git/': The requested URL returned error: 403", false)] // accepted, not authorised: no other token fixes it
    [InlineData("remote: You are not allowed to push code to this project.", false)]
    [InlineData("fatal: unable to access 'https://example.test/repo.git/': Could not resolve host: example.test", false)]
    [InlineData("fatal: could not read Username for 'https://example.test': terminal prompts disabled", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCredentialRefusal_reads_what_git_gitlab_and_the_helper_say(string? stderr, bool expected) =>
        LocalGitWorkspaceProvider.IsCredentialRefusal(stderr).ShouldBe(expected);

    [Fact]
    public void The_helpers_own_refusal_message_is_one_of_the_markers()
    {
        // Drift detector: the helper's text is written in TokenedGitCommand and read here. Reword one and not the other and
        // an LFS upload the remote refused would be retried as if nothing had been learned.
        LocalGitWorkspaceProvider.CredentialRefusalMarkers.ShouldContain(marker => TokenedGitCommand.CredentialHelper.Contains(marker, StringComparison.Ordinal));
        LocalGitWorkspaceProvider.CredentialRefusalMarkers.ShouldBe(new[] { "Authentication failed for", "HTTP Basic: Access denied", "returned error: 401", "refused this credential" }, "adding or removing a marker changes which refusals get a retry — decide it here");
    }

    // ─── Harness ─────────────────────────────────────────────────────────────────────

    private static SandboxResult Refusal(string stderr = GitRefusal) => new() { Status = SandboxStatus.Failed, ExitCode = 128, Stdout = "", Stderr = stderr };

    /// <summary>The git subcommand, past any leading <c>-c key=value</c> and <c>-C dir</c>.</summary>
    private static string Subcommand(SandboxSpec spec)
    {
        var i = 0;
        while (spec.Args[i] is "-c" or "-C") i += 2;
        return spec.Args[i];
    }

    private static async Task<PublishUnderTest> PrepareAsync(ScriptedRunner runner, ScriptedSource? source, bool repositoryNamed = true, string? cloneToken = "clone-token")
    {
        var workspacesRoot = Path.Combine(Path.GetTempPath(), "cs-credunit-" + Guid.NewGuid().ToString("N"));
        var provider = new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new[] { runner }), source, NullLogger<LocalGitWorkspaceProvider>.Instance, workspacesRoot);
        var handle = await provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest
        {
            RepositoryUrl = Url, Token = cloneToken, TokenUsername = "oauth2",
            RepositoryId = repositoryNamed ? Guid.NewGuid() : null, TeamId = repositoryNamed ? Guid.NewGuid() : null,
        }), CancellationToken.None);

        runner.MarkCloneDone();
        return new PublishUnderTest(handle, runner, workspacesRoot);
    }

    private sealed class PublishUnderTest(IWorkspaceHandle handle, ScriptedRunner runner, string workspacesRoot) : IAsyncDisposable
    {
        public ScriptedRunner Runner { get; } = runner;

        public async Task<string?> PushAsync(CancellationToken cancellationToken = default) => await ((IWorkspacePushHandle)handle).PushChangesAsync(Branch, cancellationToken);

        public string? LastPushedCommitSha() => ((IWorkspacePushHandle)handle).LastPushedCommitSha();

        /// <summary>An LFS object in the clone's store, so the publish runs the LFS upload before the push.</summary>
        public void PlantALfsObject()
        {
            var oid = "abcd" + new string('0', 60);
            var path = Path.Combine(handle.Directory, ".git", "lfs", "objects", "ab", "cd", oid);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "an lfs object the branch points at\n");
        }

        public async ValueTask DisposeAsync()
        {
            await handle.DisposeAsync();

            try { Directory.Delete(workspacesRoot, recursive: true); } catch { /* best-effort: the root holds nothing once the workspace is removed */ }
        }
    }

    /// <summary>The credential store, from a script: answers each ask with the next token (repeating the last) unless <see cref="Answer"/> decides, and counts the asks.</summary>
    private sealed class ScriptedSource : IWorkspaceTokenSource
    {
        private readonly string[] _tokens;
        private int _asked;

        public ScriptedSource(params string[] tokens) { _tokens = tokens; }

        public int Asked => Volatile.Read(ref _asked);

        /// <summary>When set, decides each ask (numbered from 1) instead of the script of tokens.</summary>
        public Func<int, string?>? Answer { get; set; }

        /// <summary>Called with the ids each ask carried, before it is answered.</summary>
        public Action<Guid, Guid>? OnAsk { get; set; }

        public Task<string?> ResolveAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ask = Interlocked.Increment(ref _asked);
            OnAsk?.Invoke(repositoryId, teamId);

            return Task.FromResult(Answer is not null ? Answer(ask) : _tokens[Math.Min(ask, _tokens.Length) - 1]);
        }
    }

    /// <summary>Answers success with a 40-char sha for every command — the local ones, and the remote ones unless <see cref="Remote"/> decides otherwise — except that the bundle is never "within the base" (the branch adds objects). Records every spec; the ones after the clone are the publish.</summary>
    private sealed class ScriptedRunner : ISandboxRunner
    {
        private int _cloneSpecs = -1;

        public string Kind => "local";

        public List<SandboxSpec> Specs { get; } = new();

        /// <summary>For a command that reaches the remote (a tokened one), the answer to give it — numbered by order among them — or null for success.</summary>
        public Func<SandboxSpec, int, SandboxResult?>? Remote { get; set; }

        public IReadOnlyList<SandboxSpec> PostCloneSpecs => Specs.Skip(_cloneSpecs).ToList();

        /// <summary>The commands after the clone that reached the remote: every tokened one (they carry the credential in their environment).</summary>
        public IReadOnlyList<SandboxSpec> RemoteSpecs => PostCloneSpecs.Where(s => s.Environment.ContainsKey("CODESPACE_GIT_PASSWORD")).ToList();

        public void MarkCloneDone() => _cloneSpecs = Specs.Count;

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);

            if (_cloneSpecs >= 0 && spec.Environment.ContainsKey("CODESPACE_GIT_PASSWORD") && Remote?.Invoke(spec, RemoteSpecs.Count) is { } scripted) return Task.FromResult(scripted);

            if (spec.Args.Contains("--is-ancestor")) return Task.FromResult(new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 1, Stdout = "", Stderr = "" });

            return Task.FromResult(new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = new string('a', 40), Stderr = "" });
        }
    }
}
