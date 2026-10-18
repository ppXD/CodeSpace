using System.Net;
using System.Net.Sockets;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The two places a repository becomes a clone request — the workspace resolver and the run-command service — against real
/// Postgres and the real provider-auth layer. When a GitLab repository is cloned WITH a credential from another origin than
/// its provider's base URL (GitLab's <c>external_url</c> disagrees with the base URL the owner typed, which is what
/// redirects), the resolve logs a Warning naming both origins; the request is unchanged — the clone URL is not rewritten,
/// the stored base URL is not touched. An anonymous clone, a clone from the base URL's own origin and a GitHub clone say nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CloneUrlAuthorityFlowTests
{
    private const string BaseUrl = "http://gitlab.acme.test";

    private readonly PostgresFixture _fixture;

    public CloneUrlAuthorityFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_resolver_warns_about_a_credentialed_clone_from_another_origin_and_leaves_the_request_as_it_was()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitLab, BaseUrl, "https://gitlab.acme.test/org/repo.git", token: "glpat_x");
        var logger = new CapturingLogger<RepositoryWorkspaceResolver>();

        var request = await ResolveAsync(teamId, repoId, logger);

        request.RepositoryUrl.ShouldBe("https://gitlab.acme.test/org/repo.git", "never rewritten");
        request.Token.ShouldBe("glpat_x");
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("https://gitlab.acme.test:443");
        warning.ShouldContain("http://gitlab.acme.test:80");
        (await LoadBaseUrlAsync(repoId)).ShouldBe(BaseUrl, "the stored base URL is never rewritten");
    }

    [Theory]
    [InlineData(ProviderKind.GitLab, "http://gitlab.acme.test/org/repo.git", "glpat_x")]       // the base URL's own origin
    [InlineData(ProviderKind.GitLab, "http://GITLAB.acme.test:80/org/repo.git", "glpat_x")]     // another spelling of it
    [InlineData(ProviderKind.GitLab, "https://gitlab.acme.test/org/repo.git", null)]            // anonymous: a redirect harms nothing
    [InlineData(ProviderKind.GitHub, "https://github.example.test/org/repo.git", "ghp_x")]      // GitHub's hosts differ by design
    public async Task The_resolver_says_nothing_when_there_is_nothing_to_say(ProviderKind provider, string cloneUrl, string? token)
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, provider, BaseUrl, cloneUrl, token);
        var logger = new CapturingLogger<RepositoryWorkspaceResolver>();

        await ResolveAsync(teamId, repoId, logger);

        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_run_command_service_warns_the_same_way_before_it_clones()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var closed = ClosedLoopbackPort();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitLab, $"http://127.0.0.1:{closed + 1}", $"http://127.0.0.1:{closed}/org/repo.git", token: "glpat_x");
        var logger = new CapturingLogger<RunCommandService>();

        // The clone itself fails (nothing listens on the port), which is the shape of the failure the warning explains.
        await Should.ThrowAsync<WorkspaceException>(async () =>
        {
            using var scope = _fixture.BeginScope(b => b.RegisterInstance<ILogger<RunCommandService>>(logger));
            await scope.Resolve<IRunCommandService>().RunAsync(new RunCommandRequest { RepositoryId = repoId, TeamId = teamId, Command = "true" }, CancellationToken.None);
        });

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain($"http://127.0.0.1:{closed}");
        warning.ShouldContain($"http://127.0.0.1:{closed + 1}");
    }

    private async Task<WorkspaceRequest> ResolveAsync(Guid teamId, Guid repoId, CapturingLogger<RepositoryWorkspaceResolver> logger)
    {
        using var scope = _fixture.BeginScope(b => b.RegisterInstance<ILogger<RepositoryWorkspaceResolver>>(logger));

        return (await scope.Resolve<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(repoId, teamId, CancellationToken.None))!;
    }

    private async Task<string> LoadBaseUrlAsync(Guid repoId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var repo = await db.Repository.FindAsync(repoId);

        return (await db.ProviderInstance.FindAsync(repo!.ProviderInstanceId))!.BaseUrl;
    }

    private static int ClosedLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedRepoAsync(Guid teamId, ProviderKind provider, string baseUrl, string cloneUrl, string? token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = provider, DisplayName = provider.ToString(), BaseUrl = baseUrl });

        Guid? credentialId = null;
        if (token is not null)
        {
            credentialId = Guid.NewGuid();
            db.Credential.Add(new Credential
            {
                Id = credentialId.Value, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "cred", Status = CredentialStatus.Active,
                EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token })),
            });
        }

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = $"{baseUrl}/org/repo",
        });

        await db.SaveChangesAsync();

        return repoId;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }
}
