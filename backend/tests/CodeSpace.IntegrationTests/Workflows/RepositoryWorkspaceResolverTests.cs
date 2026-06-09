using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// <see cref="RepositoryWorkspaceResolver"/> against real Postgres + the real provider-auth layer: a
/// bound repository + credential resolve into the <see cref="WorkspaceRequest"/> the clone consumes — the
/// HTTPS URL, the default branch, a token resolved through the same auth strategies the providers use, and
/// the provider-appropriate basic-auth username. Pins: GitHub vs GitLab username; an anonymous (no-
/// credential) repo; no-RepositoryId → no workspace; an unknown / cross-team repo is refused.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryWorkspaceResolverTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryWorkspaceResolverTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Resolves_a_github_repo_with_a_token_and_x_access_token_username()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");

        var request = await ResolveAsync(TaskFor(repoId), teamId);

        request.ShouldNotBeNull();
        request!.RepositoryUrl.ShouldBe("https://github.com/org/repo.git");
        request.Ref.ShouldBe("main");
        request.Token.ShouldBe("ghp_abc");
        request.TokenUsername.ShouldBe("x-access-token");
    }

    [Fact]
    public async Task Gitlab_repo_uses_the_oauth2_username_and_default_branch()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitLab, "https://gitlab.com/org/repo.git", "develop", token: "glpat_x");

        var request = await ResolveAsync(TaskFor(repoId), teamId);

        request!.Ref.ShouldBe("develop");
        request.Token.ShouldBe("glpat_x");
        request.TokenUsername.ShouldBe("oauth2");
    }

    [Fact]
    public async Task A_repo_without_a_credential_resolves_an_anonymous_clone()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/public.git", "main", token: null);

        var request = await ResolveAsync(TaskFor(repoId), teamId);

        request!.Token.ShouldBeNull();
        request.TokenUsername.ShouldBeNull("no token → no basic-auth username");
    }

    [Fact]
    public async Task No_repository_id_resolves_to_no_workspace()
    {
        var teamId = await SeedTeamAsync();

        (await ResolveAsync(new AgentTask { Goal = "g", Harness = "h", Model = "m" }, teamId)).ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_repository_is_refused()
    {
        var teamId = await SeedTeamAsync();

        await Should.ThrowAsync<WorkspaceException>(async () => await ResolveAsync(TaskFor(Guid.NewGuid()), teamId));
    }

    [Fact]
    public async Task A_repo_belonging_to_another_team_is_refused()
    {
        var teamId = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(otherTeam, ProviderKind.GitHub, "https://github.com/org/r.git", "main", token: "t");

        await Should.ThrowAsync<WorkspaceException>(async () => await ResolveAsync(TaskFor(repoId), teamId));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static AgentTask TaskFor(Guid repositoryId) => new() { Goal = "g", Harness = "h", Model = "m", RepositoryId = repositoryId };

    private async Task<WorkspaceRequest?> ResolveAsync(AgentTask task, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentWorkspaceResolver>().ResolveAsync(task, teamId, CancellationToken.None);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"ws-{userId:N}@test.local", Name = "ws" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"ws-{teamId:N}", Name = "WS", Kind = TeamKind.Workspace, OwnerUserId = userId });

        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task<Guid> SeedRepoAsync(Guid teamId, ProviderKind provider, string cloneUrl, string defaultBranch, string? token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = provider, DisplayName = provider.ToString(), BaseUrl = "https://host" });

        Guid? credentialId = null;
        if (token is not null)
        {
            var encryptor = scope.Resolve<IPayloadEncryptor>();
            var serializer = scope.Resolve<ICredentialPayloadSerializer>();

            credentialId = Guid.NewGuid();
            db.Credential.Add(new Credential
            {
                Id = credentialId.Value, TeamId = teamId, ProviderInstanceId = instanceId,
                AuthType = AuthType.Pat, DisplayName = "cred", Status = CredentialStatus.Active,
                EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token })),
            });
        }

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrl, WebUrl = "https://host/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }
}
