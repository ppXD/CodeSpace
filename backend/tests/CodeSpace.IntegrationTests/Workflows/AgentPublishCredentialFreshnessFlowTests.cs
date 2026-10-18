using System.Reflection;
using System.Text.Json;
using System.Web;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): a run's last act — pushing the agent's branch — against a git remote that behaves like GitLab,
/// with the credential chain COMPOSED for real: the workspace resolver provisions the clone, the REAL
/// <see cref="RepositoryWorkspaceTokenSource"/> re-resolves the credential before the push through the real
/// provider auth resolver, OAuth strategy, refresher, Postgres advisory lock and payload writer, against a loopback token
/// endpoint that rotates single-use refresh tokens, over a real <see cref="LocalGitWorkspaceProvider"/> and real <c>git</c>.
/// The remote revokes the access token it issued the moment the token endpoint issues a new one — GitLab's behaviour, and
/// the reason a run that outlives its token, or whose token a sibling run refreshed, used to push with a dead one.
///
/// <para>Probe P1, composed: the clone is made with the access token the run was provisioned with; the agent works while the
/// stored pair ages into its refresh window; the push finds the token due, refreshes it, and lands the branch. On
/// the code before the token source existed the same shape ended in <c>git push --force … failed (exit 128): fatal:
/// Authentication failed</c>, which the executor swallowed into a Warning on a Succeeded run.</para>
///
/// <para>A run's own clock is not what moves here: the stored pair is aged by rewriting its recorded expiry, which is what the
/// refresher reads, so no test waits for a token to age. Each test owns GUID-named rows and a temp tree, removes the tree on
/// every path, and skips on Windows or without git.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentPublishCredentialFreshnessFlowTests
{
    private const string UsernameForGitLab = "oauth2";

    private readonly PostgresFixture _fixture;

    public AgentPublishCredentialFreshnessFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_token_that_ages_into_its_refresh_window_while_the_agent_works_is_refreshed_for_the_push()
    {
        if (!await CanRunGitAsync()) return;

        using var ctx = await FreshnessContext.StartAsync(_fixture, liveRefreshToken: "rt-0");
        await using var handle = await ctx.ProvisionAsync();
        ctx.ProvisionedToken.ShouldBe("at-0", "fixture check: the clone was made with the access token the run was provisioned with");

        await ctx.AgeStoredPairAsync(secondsLeft: 30);   // the agent has worked for most of the token's life
        await ctx.AgentWritesAsync(handle.Directory);

        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        (await ctx.RemoteHasBranchAsync()).ShouldBeTrue("the branch landed on the remote");
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty("the token the clone carried was never presented at the push — it was due for a refresh, so the push refreshed it first");
        ctx.Endpoint.Requests.ShouldBe(1, "exactly one refresh: the push's — the readback finds the new token live");
        (await ctx.StoredAsync()).RefreshToken.ShouldBe("rt-1", "the rotated pair is what the row holds, so the next run refreshes with a live refresh token");
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldNotBeNull("the readback was authenticated with the refreshed token too");
    }

    [Fact]
    public async Task A_token_another_run_rotated_while_this_one_worked_is_replaced_by_the_one_it_stored()
    {
        // GitLab revokes the old access token on ANY refresh, so a sibling run (or a webhook admission) that refreshed the
        // same credential killed the token this run holds — long before its own expiry, with nothing for this run to notice.
        if (!await CanRunGitAsync()) return;

        using var ctx = await FreshnessContext.StartAsync(_fixture, liveRefreshToken: "rt-0");
        await using var handle = await ctx.ProvisionAsync();

        await ctx.AnotherRunRefreshesAsync();
        ctx.Endpoint.Requests.ShouldBe(1, "fixture check: the sibling's refresh reached the token endpoint");
        await ctx.AgentWritesAsync(handle.Directory);

        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty("the revoked token was not presented: the push read the pair the sibling stored");
        ctx.Endpoint.Requests.ShouldBe(1, "this run did not refresh again — the stored token was live");
    }

    [Fact]
    public async Task A_personal_access_token_bound_repository_pushes_with_its_token_unchanged()
    {
        if (!await CanRunGitAsync()) return;

        using var ctx = await FreshnessContext.StartAsync(_fixture, liveRefreshToken: null, patToken: "glpat-fake-personal-token");
        await using var handle = await ctx.ProvisionAsync();
        ctx.ProvisionedToken.ShouldBe("glpat-fake-personal-token");

        await ctx.AgentWritesAsync(handle.Directory);
        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        (await ctx.RemoteHasBranchAsync()).ShouldBeTrue();
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty();
        ctx.Endpoint.Requests.ShouldBe(0, "a static token has nothing to refresh");
    }

    [Fact]
    public async Task A_credential_disconnected_while_the_agent_worked_is_a_typed_rejection_and_nothing_is_presented_to_the_remote()
    {
        if (!await CanRunGitAsync()) return;

        using var ctx = await FreshnessContext.StartAsync(_fixture, liveRefreshToken: "rt-0");
        await using var handle = await ctx.ProvisionAsync();

        await ctx.DisconnectAsync();
        await ctx.AgentWritesAsync(handle.Directory);

        var failure = await Should.ThrowAsync<GitCredentialRejectedException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        failure.Message.ShouldContain("was disconnected");
        failure.Message.ShouldContain("Reconnect the provider");
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty("a disconnected credential is never put to the remote");
        (await ctx.RemoteHasBranchAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_repository_deleted_while_the_agent_worked_is_not_pushed_to()
    {
        // Fail closed, as the provisioning resolve does: an operator who removed the repository does not expect a run that
        // started earlier to still publish to it through a credential that has not expired.
        if (!await CanRunGitAsync()) return;

        using var ctx = await FreshnessContext.StartAsync(_fixture, liveRefreshToken: "rt-0");
        await using var handle = await ctx.ProvisionAsync();

        await ctx.DeleteRepositoryAsync();
        await ctx.AgentWritesAsync(handle.Directory);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Message.ShouldContain("not found for this team");
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty();
        (await ctx.RemoteHasBranchAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refresh_the_provider_refuses_is_a_workspace_failure_the_push_retry_can_see_not_a_raw_oauth_exception()
    {
        // The stored refresh token is one the provider no longer honours (rotated elsewhere, grant revoked). Turning that
        // into a typed 'reconnect' is the refresher's own arc; what this pins is that the push path stays inside its own
        // exception contract — a WorkspaceException — instead of letting an OAuth exception escape the publish and flip the
        // run that produced the work.
        if (!await CanRunGitAsync()) return;

        using var ctx = await FreshnessContext.StartAsync(_fixture, liveRefreshToken: "rt-held-by-someone-else");
        await using var handle = await ctx.ProvisionAsync();

        await ctx.AgeStoredPairAsync(secondsLeft: 30);
        await ctx.AgentWritesAsync(handle.Directory);

        var failure = await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Message.ShouldContain("Could not resolve the git credential to push with");
        failure.Message.ShouldContain("invalid_grant", Case.Insensitive);
        failure.Message.ShouldNotContain("at-0", Case.Insensitive, "the token is never in the message");
        ctx.Remote.RefusedAuthorizations.ShouldBeEmpty();
    }

    [Fact]
    public void The_container_hands_the_workspace_provider_the_token_source()
    {
        // The whole fix is inert if the container builds the provider through the constructor that has no token source: every
        // push would carry the captured token again, with every other test here still green. Autofac picks the widest
        // constructor it can satisfy, so this pins that it finds the one with the source.
        using var scope = _fixture.BeginScope();

        var provider = scope.Resolve<IWorkspaceProviderRegistry>().Resolve(SandboxKinds.Local).ShouldBeOfType<LocalGitWorkspaceProvider>();
        var source = typeof(LocalGitWorkspaceProvider).GetField("_tokenSource", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull().GetValue(provider);

        source.ShouldBeOfType<RepositoryWorkspaceTokenSource>("the registered provider must push with a freshly resolved credential");
        scope.Resolve<IWorkspaceTokenSource>().ShouldBeSameAs(source, "the provider and the container share one source");
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────

    private static async Task<bool> CanRunGitAsync()
    {
        if (OperatingSystem.IsWindows()) return false;

        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
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

    /// <summary>
    /// GitLab's token endpoint for <c>grant_type=refresh_token</c>: each refresh token is single-use — presenting the live one
    /// rotates it (<c>rt-N</c> → <c>rt-N+1</c>, access token <c>at-N+1</c>, valid two hours) and revokes the previous access
    /// token, which <paramref name="onRotated"/> relays to the git remote; presenting any other is refused with GitLab's own
    /// <c>invalid_grant</c> body.
    /// </summary>
    private sealed class RotatingEndpoint
    {
        private const string InvalidGrantBody = """{"error":"invalid_grant","error_description":"The provided authorization grant is invalid, expired, revoked, does not match the redirection URI used in the authorization request, or was issued to another client."}""";

        private readonly object _gate = new();
        private readonly StubProviderHost _provider;
        private readonly Action<string> _onRotated;
        private string? _liveRefreshToken;

        private RotatingEndpoint(StubProviderHost provider, string? liveRefreshToken, Action<string> onRotated)
        {
            _provider = provider;
            _liveRefreshToken = liveRefreshToken;
            _onRotated = onRotated;
        }

        public static RotatingEndpoint Serve(StubProviderHost provider, string? liveRefreshToken, Action<string> onRotated)
        {
            var endpoint = new RotatingEndpoint(provider, liveRefreshToken, onRotated);
            provider.Answer("POST", "/oauth/token", endpoint.Refresh);
            return endpoint;
        }

        /// <summary>Refresh requests that reached the endpoint, rotated or refused.</summary>
        public int Requests => _provider.Requests.Count(r => r.PathAndQuery.StartsWith("/oauth/token", StringComparison.Ordinal));

        private StubProviderHost.StubReply Refresh(StubProviderHost.RecordedRequest request)
        {
            var presented = HttpUtility.ParseQueryString(request.Body)["refresh_token"];

            lock (_gate)
            {
                if (presented is null || presented != _liveRefreshToken) return new StubProviderHost.StubReply(400, InvalidGrantBody);

                var generation = int.Parse(_liveRefreshToken["rt-".Length..]) + 1;
                _liveRefreshToken = $"rt-{generation}";
                _onRotated($"at-{generation}");

                return new StubProviderHost.StubReply(200, JsonSerializer.Serialize(new { access_token = $"at-{generation}", token_type = "Bearer", expires_in = 7200, refresh_token = _liveRefreshToken, scope = "api" }));
            }
        }
    }

    private sealed record Seeded(Guid UserId, Guid TeamId, Guid CredentialId, Guid RepositoryId);

    private sealed class FreshnessContext : IDisposable
    {
        private readonly PostgresFixture _fixture;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-credfresh-" + Guid.NewGuid().ToString("N"));
        private readonly StubProviderHost _provider = new();
        private readonly ILifetimeScope _sourceScope;
        private Seeded _seed = null!;

        private FreshnessContext(PostgresFixture fixture, string? liveRefreshToken)
        {
            _fixture = fixture;
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Endpoint = RotatingEndpoint.Serve(_provider, liveRefreshToken, access => Remote.AcceptOnly(UsernameForGitLab, access));
            _sourceScope = fixture.BeginScope();

            PushProvider = new LocalGitWorkspaceProvider(
                new SandboxRunnerRegistry(new ISandboxRunner[] { new HermeticRunner(Path.Combine(_root, "home")) }),
                new RepositoryWorkspaceTokenSource(_sourceScope.Resolve<IServiceScopeFactory>()),
                NullLogger<LocalGitWorkspaceProvider>.Instance,
                Path.Combine(_root, "workspaces"));
        }

        /// <summary>Reads authenticate too, as a private GitLab project's do, so the clone itself proves the token it was given.</summary>
        public GitPublishRemoteFixture Remote { get; } = new() { AuthenticateReads = true };
        public RotatingEndpoint Endpoint { get; }
        public LocalGitWorkspaceProvider PushProvider { get; }
        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        /// <summary>The access token the clone was provisioned with (the one the resolver handed the request).</summary>
        public string? ProvisionedToken { get; private set; }

        public static async Task<FreshnessContext> StartAsync(PostgresFixture fixture, string? liveRefreshToken, string? patToken = null)
        {
            var ctx = new FreshnessContext(fixture, liveRefreshToken);
            await ctx.Remote.StartAsync();
            await ctx.SeedAsync(patToken);
            ctx.Remote.AcceptOnly(UsernameForGitLab, patToken ?? "at-0");   // the token the run is provisioned with is the live one — until it is rotated
            return ctx;
        }

        /// <summary>The workspace the way the executor provisions it: the REAL resolver builds the request (token, repository id, team id), the provider clones it.</summary>
        public async Task<IWorkspaceHandle> ProvisionAsync()
        {
            using var scope = _fixture.BeginScope();
            var request = await scope.Resolve<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(_seed.RepositoryId, _seed.TeamId, CancellationToken.None).ConfigureAwait(false);

            request.ShouldNotBeNull();
            request.RepositoryId.ShouldBe(_seed.RepositoryId, "fixture check: the resolver names the repository it resolved the token for");
            request.TeamId.ShouldBe(_seed.TeamId);
            ProvisionedToken = request.Token;

            return await PushProvider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(request with { Depth = 1 }), CancellationToken.None).ConfigureAwait(false);
        }

        public Task AgentWritesAsync(string cloneDir) => File.WriteAllTextAsync(Path.Combine(cloneDir, "agent.txt"), "the agent's work\n");

        public async Task<bool> RemoteHasBranchAsync() =>
            !string.IsNullOrWhiteSpace(await GitPublishRemoteFixture.GitAsync(_root, new[] { "--git-dir", Remote.Remote, "for-each-ref", $"refs/heads/{BranchName}" }).ConfigureAwait(false));

        /// <summary>Time passes for the stored pair: the same tokens, now recorded as expiring in <paramref name="secondsLeft"/> — inside the refresher's two-minute window.</summary>
        public async Task AgeStoredPairAsync(int secondsLeft)
        {
            var stored = await StoredAsync().ConfigureAwait(false);

            await StoreAsync(new OAuthPayload { AccessToken = stored.AccessToken, RefreshToken = stored.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(secondsLeft) }).ConfigureAwait(false);
        }

        /// <summary>What a sibling run does on finding the token due: refresh against the provider, store the rotated pair. The provider has by then revoked the access token this run holds.</summary>
        public async Task AnotherRunRefreshesAsync()
        {
            using var scope = _fixture.BeginScope();
            var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Include(c => c.ProviderInstance).SingleAsync(c => c.Id == _seed.CredentialId).ConfigureAwait(false);
            var current = (OAuthPayload)scope.Resolve<ICredentialResolver>().Resolve(credential);
            var instance = credential.ProviderInstance;

            var response = await scope.Resolve<IOAuthClientRegistry>().Get(ProviderKind.GitLab).RefreshAsync(new OAuthRefreshInput
            {
                Instance = instance, ClientId = instance.OauthClientId!, ClientSecret = scope.Resolve<IPayloadEncryptor>().Decrypt(instance.OauthClientSecretEnc!), RefreshToken = current.RefreshToken!,
            }, CancellationToken.None).ConfigureAwait(false);

            await StoreAsync(new OAuthPayload { AccessToken = response.AccessToken, RefreshToken = response.RefreshToken, ExpiresAt = response.ExpiresAt }).ConfigureAwait(false);
        }

        public async Task<OAuthPayload> StoredAsync()
        {
            using var scope = _fixture.BeginScope();
            var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == _seed.CredentialId).ConfigureAwait(false);

            return (OAuthPayload)scope.Resolve<ICredentialResolver>().Resolve(credential);
        }

        private async Task StoreAsync(OAuthPayload payload)
        {
            using var scope = _fixture.BeginScope();
            var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == _seed.CredentialId).ConfigureAwait(false);

            var stored = await scope.Resolve<ICredentialPayloadWriter>().UpdatePayloadAsync(credential, credential.EncryptedPayload, payload, CancellationToken.None).ConfigureAwait(false);

            stored.ShouldBeTrue("nothing else holds the refresh lock, so the row should still hold what it read");
        }

        /// <summary>The user disconnecting the credential: the real revoke command, as an admin of its team. Its own call to the provider's revoke endpoint goes to a recording client.</summary>
        public async Task DisconnectAsync()
        {
            using var scope = _fixture.BeginScopeAs(_seed.UserId, _seed.TeamId, Roles.Admin);
            using var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleClientRegistry(new StubOAuthClient(ProviderKind.GitLab, new OAuthTokenResponse { AccessToken = "unused" }))).SingleInstance());

            await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = _seed.CredentialId }).ConfigureAwait(false);
        }

        public async Task DeleteRepositoryAsync()
        {
            using var scope = _fixture.BeginScope();

            await scope.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == _seed.RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        private async Task SeedAsync(string? patToken)
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var encryptor = scope.Resolve<IPayloadEncryptor>();
            var serializer = scope.Resolve<ICredentialPayloadSerializer>();

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
            var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
            var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = _provider.BaseUrl, OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret") };

            CredentialPayload payload = patToken is null
                ? new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) }
                : new PatPayload { Token = patToken };

            var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, AuthType = payload.Type, DisplayName = "gitlab credential", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = (payload as OAuthPayload)?.ExpiresAt, Status = CredentialStatus.Active };
            var repository = new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = suffix, NamespacePath = "acme", Name = "repo", FullPath = "acme/repo", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{_provider.BaseUrl}/acme/repo", CloneUrlHttps = Remote.Url, Status = RepositoryStatus.Active };

            db.User.Add(user);
            db.Team.Add(team);
            db.ProviderInstance.Add(instance);
            db.Credential.Add(credential);
            db.Repository.Add(repository);
            await db.SaveChangesAsync().ConfigureAwait(false);

            _seed = new Seeded(user.Id, team.Id, credential.Id, repository.Id);
        }

        public void Dispose()
        {
            _sourceScope.Dispose();
            Remote.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _provider.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class SingleClientRegistry(IOAuthClient client) : IOAuthClientRegistry
    {
        public IOAuthClient Get(ProviderKind kind) => client;
    }
}
