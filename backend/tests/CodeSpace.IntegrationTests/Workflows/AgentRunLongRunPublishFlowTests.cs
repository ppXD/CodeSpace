using System.Text.Json;
using System.Web;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12), end to end: the REAL <see cref="IAgentRunExecutor"/> — real Postgres, a real <c>/bin/sh</c>
/// harness, the real resolver → token source → provider auth → OAuth refresher chain, real <c>git</c> — runs a LONG run on a
/// GitLab OAuth-bound repository against a remote that revokes the access token it issued the moment a new one exists.
/// The harness holds the run open until the test has done, from outside, what a sibling run does to a credential in
/// production: refreshed it, so the token the run was provisioned with is dead by the time its last act — the push — runs.
///
/// <para>The run must publish its branch; and when no credential the store can offer is one the remote accepts, the run keeps
/// its Succeeded status — it did its work, and every caller that reads Succeeded must keep reading it — while the failure
/// is recorded as a typed, durable publish failure (code, message, manifest, timeline) instead of a Warning nobody reads.</para>
///
/// <para>Only the workspace provider's git runner is isolated from the host's git config; everything else is the production
/// composition. Each test owns GUID-named rows, a marker directory and a temp tree, removes them on every path, and skips on
/// Windows or without git.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLongRunPublishFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunLongRunPublishFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_run_that_outlived_its_access_token_still_publishes_its_branch()
    {
        if (!await CanRunGitAsync()) return;

        using var world = await LongRunWorld.StartAsync(_fixture);

        var runId = await world.RunUntilTheTokenIsRevokedAsync(refreshAsASibling: true);

        var run = await world.ReadRunAsync(runId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ProducedBranch.ShouldBe(AgentRunExecutor.BuildBranchName(runId), "the branch published, though the token the run was provisioned with was revoked before the push");
        result.PublishError.ShouldBeNull();
        result.PublishFailureCode.ShouldBeNull();
        result.PushedCommitSha.ShouldNotBeNull("the readback confirmed the remote tip with the fresh credential too");

        (await world.RemoteHasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeTrue("the remote carries the branch");
        world.Remote.RefusedAuthorizations.ShouldBeEmpty("the revoked token was never presented to the remote at the push");
    }

    [Fact]
    public async Task A_run_whose_credential_the_remote_refuses_stays_succeeded_with_a_typed_recorded_publish_failure()
    {
        if (!await CanRunGitAsync()) return;

        using var world = await LongRunWorld.StartAsync(_fixture);

        // After the sibling's refresh the remote accepts a token the store has never held — as when the grant was revoked at
        // the provider by an administrator: nothing the store can answer works, however many times it is asked.
        var runId = await world.RunUntilTheTokenIsRevokedAsync(refreshAsASibling: false);

        var run = await world.ReadRunAsync(runId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the run did its work; the publish is the side effect that failed — callers that read Succeeded keep reading it");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ProducedBranch.ShouldBeNull();
        result.PublishFailureCode.ShouldBe("git_credential_rejected", "recorded on the run, typed");
        result.PublishError.ShouldNotBeNullOrEmpty();
        result.PublishError.ShouldContain("Reconnect the provider", customMessage: "the recorded failure says what to do about it");
        result.PublishError.ShouldNotContain("at-0", Case.Insensitive, "no token reaches the record");
        result.ChangedFiles.ShouldContain("agent-change.txt", "the agent's work is still captured — the patch is what a reviewer can still use");

        var manifest = await world.ReadPublishManifestAsync(runId);
        manifest.PublishError.ShouldNotBeNullOrEmpty("the durable publish record carries the failure too");
        manifest.PublishError.ShouldContain("Reconnect the provider");
        manifest.Branch.ShouldBeNull();

        (await world.ReadEventsAsync(runId)).ShouldContain(e => e.Kind == AgentEventKind.Warning && e.Text!.StartsWith("Could not push the agent's changes to a branch"), "the timeline says why no branch appeared");

        (await world.RemoteHasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeFalse();
        world.Remote.RefusedAuthorizations.Count.ShouldBe(1, "the credential the store offered was presented once and not again: a refusal resolved to the same credential is not retried");
    }

    // ── World ──────────────────────────────────────────────────────────────────────

    private static async Task<bool> CanRunGitAsync()
    {
        if (OperatingSystem.IsWindows()) return false;

        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A hermetic git environment for the workspace provider: no operator config, no prompt, a HOME of its own.</summary>
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

    /// <summary>A shell harness that records when it started, does its edit, then holds the run open until released — a run long enough for the world to change under it.</summary>
    private sealed class HoldingHarness(string startedMarker, string releaseMarker) : IAgentHarness
    {
        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new()
        {
            Command = "/bin/sh",
            Args = new[] { "-c", $"printf 'by the agent\\n' > agent-change.txt; : > '{startedMarker}'; while [ ! -f '{releaseMarker}' ]; do sleep 0.1; done; echo edited" },
            WorkingDirectory = task.WorkspaceDirectory,
            TimeoutSeconds = task.TimeoutSeconds,
        };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => ScriptedFolders.Result();
    }

    private sealed class LongRunWorld : IDisposable
    {
        private const string InvalidGrantBody = """{"error":"invalid_grant","error_description":"The provided authorization grant is invalid, expired, revoked, does not match the redirection URI used in the authorization request, or was issued to another client."}""";

        private readonly PostgresFixture _fixture;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-longrun-" + Guid.NewGuid().ToString("N"));
        private readonly StubProviderHost _provider = new();
        private readonly object _gate = new();
        private string _liveRefreshToken = "rt-0";
        private Guid _teamId;
        private Guid _credentialId;
        private Guid _repositoryId;

        private LongRunWorld(PostgresFixture fixture)
        {
            _fixture = fixture;
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "markers"));
        }

        /// <summary>Reads authenticate too, as a private GitLab project's do, so the clone itself proves the token it was given.</summary>
        public GitPublishRemoteFixture Remote { get; } = new() { AuthenticateReads = true };

        public static async Task<LongRunWorld> StartAsync(PostgresFixture fixture)
        {
            var world = new LongRunWorld(fixture);
            await world.Remote.StartAsync();
            world.Remote.AcceptOnly("oauth2", "at-0");   // the token the run is provisioned with is the live one — until it is rotated
            world._provider.Answer("POST", "/oauth/token", world.Refresh);
            await world.SeedAsync();
            return world;
        }

        /// <summary>
        /// Run the executor against the world: the harness holds the run open once the workspace is cloned; the test then does
        /// what happens to a long run's credential — a sibling refreshes it (the provider revokes the token this run holds and
        /// the remote stops accepting it) — and releases the harness, so the push is the run's next act. With
        /// <paramref name="refreshAsASibling"/> false the rotated pair never reaches the store, so the only token it can offer
        /// is one the remote has already refused.
        /// </summary>
        public async Task<Guid> RunUntilTheTokenIsRevokedAsync(bool refreshAsASibling)
        {
            var runId = await CreateRunAsync();
            var started = Path.Combine(_root, "markers", "started");
            var release = Path.Combine(_root, "markers", "release");

            var executing = Task.Run(() => ExecuteAsync(runId, new HoldingHarness(started, release)));

            await WaitForAsync(started, executing).ConfigureAwait(false);

            if (refreshAsASibling) await AnotherRunRefreshesAsync().ConfigureAwait(false);
            else Remote.AcceptOnly("oauth2", "a-token-the-store-never-held");

            await File.WriteAllTextAsync(release, "go").ConfigureAwait(false);
            await executing.WaitAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(false);

            return runId;
        }

        public async Task<AgentRun> ReadRunAsync(Guid runId)
        {
            using var scope = _fixture.BeginScope();

            return await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<PublishManifest> ReadPublishManifestAsync(Guid runId)
        {
            using var scope = _fixture.BeginScope();

            return await scope.Resolve<CodeSpaceDbContext>().PublishManifest.AsNoTracking().SingleAsync(m => m.AgentRunId == runId).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<AgentRunEvent>> ReadEventsAsync(Guid runId)
        {
            using var scope = _fixture.BeginScope();

            return await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, _teamId, 0, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            !string.IsNullOrWhiteSpace(await GitPublishRemoteFixture.GitAsync(_root, new[] { "--git-dir", Remote.Remote, "for-each-ref", $"refs/heads/{branch}" }).ConfigureAwait(false));

        /// <summary>GitLab's token endpoint: the live refresh token rotates (<c>rt-N</c> → <c>rt-N+1</c>, access token <c>at-N+1</c>) and the access token it replaces is revoked — at the git remote too; any other refresh token is refused.</summary>
        private StubProviderHost.StubReply Refresh(StubProviderHost.RecordedRequest request)
        {
            var presented = HttpUtility.ParseQueryString(request.Body)["refresh_token"];

            lock (_gate)
            {
                if (presented is null || presented != _liveRefreshToken) return new StubProviderHost.StubReply(400, InvalidGrantBody);

                var generation = int.Parse(_liveRefreshToken["rt-".Length..]) + 1;
                _liveRefreshToken = $"rt-{generation}";
                Remote.AcceptOnly("oauth2", $"at-{generation}");

                return new StubProviderHost.StubReply(200, JsonSerializer.Serialize(new { access_token = $"at-{generation}", token_type = "Bearer", expires_in = 7200, refresh_token = _liveRefreshToken, scope = "api" }));
            }
        }

        private static async Task WaitForAsync(string marker, Task executing)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

            while (!File.Exists(marker))
            {
                if (executing.IsCompleted) await executing.ConfigureAwait(false);   // surfaces why the executor ended before the harness started

                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"The harness never started within 60s — the run did not get past provisioning its workspace. Inspect the run's events and the clone under {Path.GetTempPath()}codespace-agent-workspaces.");

                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        /// <summary>What a sibling run does on finding the token due: refresh against the provider and store the rotated pair.</summary>
        private async Task AnotherRunRefreshesAsync()
        {
            using var scope = _fixture.BeginScope();
            var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Include(c => c.ProviderInstance).SingleAsync(c => c.Id == _credentialId).ConfigureAwait(false);
            var current = (OAuthPayload)scope.Resolve<ICredentialResolver>().Resolve(credential);
            var instance = credential.ProviderInstance;

            var response = await scope.Resolve<IOAuthClientRegistry>().Get(ProviderKind.GitLab).RefreshAsync(new OAuthRefreshInput
            {
                Instance = instance, ClientId = instance.OauthClientId!, ClientSecret = scope.Resolve<IPayloadEncryptor>().Decrypt(instance.OauthClientSecretEnc!), RefreshToken = current.RefreshToken!,
            }, CancellationToken.None).ConfigureAwait(false);

            var stored = await scope.Resolve<ICredentialPayloadWriter>().UpdatePayloadAsync(credential, credential.EncryptedPayload, new OAuthPayload { AccessToken = response.AccessToken, RefreshToken = response.RefreshToken, ExpiresAt = response.ExpiresAt }, CancellationToken.None).ConfigureAwait(false);

            stored.ShouldBeTrue("nothing else holds the refresh lock, so the row should still hold what it read");
        }

        private async Task<Guid> CreateRunAsync()
        {
            using var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, _teamId).ConfigureAwait(false);
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = _repositoryId }, _teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return run.Id;
        }

        /// <summary>The executor as production composes it — except the workspace provider's git runner, isolated from the host's git config.</summary>
        private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
        {
            using var scope = _fixture.BeginScope();

            var provider = new LocalGitWorkspaceProvider(
                new SandboxRunnerRegistry(new ISandboxRunner[] { new HermeticRunner(Path.Combine(_root, "home")) }),
                scope.Resolve<IWorkspaceTokenSource>(),
                NullLogger<LocalGitWorkspaceProvider>.Instance,
                Path.Combine(_root, "workspaces"));

            var executor = new AgentRunExecutor(
                scope.Resolve<IAgentRunService>(),
                new AgentHarnessRegistry(new[] { harness }),
                new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
                scope.Resolve<ISandboxRunnerRegistry>(),
                scope.Resolve<IAgentWorkspaceResolver>(),
                scope.Resolve<IModelCredentialResolver>(),
                new WorkspaceProviderRegistry(new IWorkspaceProvider[] { provider }),
                scope.Resolve<IAgentRunCompletionNotifier>(),
                scope.Resolve<IServiceScopeFactory>(),
                scope.Resolve<CodeSpaceDbContext>(),
                scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
                scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
                scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
                NullLogger<AgentRunExecutor>.Instance);

            await executor.ExecuteAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task SeedAsync()
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var encryptor = scope.Resolve<IPayloadEncryptor>();
            var serializer = scope.Resolve<ICredentialPayloadSerializer>();

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var userId = Guid.NewGuid();
            _teamId = Guid.NewGuid();
            db.User.Add(new User { Id = userId, Email = $"agent-{suffix}@test.local", Name = $"agent-{suffix}" });
            db.Team.Add(new Team { Id = _teamId, Slug = $"agent-{suffix}", Name = "Agent Team", Kind = TeamKind.Workspace });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = _teamId, UserId = userId, Role = TeamRole.Owner });

            var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = _teamId, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = _provider.BaseUrl, OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret") };
            var payload = new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) };
            var credential = new Credential { Id = Guid.NewGuid(), TeamId = _teamId, ProviderInstanceId = instance.Id, AuthType = AuthType.OAuth, DisplayName = "gitlab oauth", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = CredentialStatus.Active };
            var repository = new Repository { Id = Guid.NewGuid(), TeamId = _teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = suffix, NamespacePath = "acme", Name = "repo", FullPath = "acme/repo", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{_provider.BaseUrl}/acme/repo", CloneUrlHttps = Remote.Url, Status = RepositoryStatus.Active };

            db.ProviderInstance.Add(instance);
            db.Credential.Add(credential);
            db.Repository.Add(repository);
            await db.SaveChangesAsync().ConfigureAwait(false);

            _credentialId = credential.Id;
            _repositoryId = repository.Id;
        }

        public void Dispose()
        {
            Remote.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _provider.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
