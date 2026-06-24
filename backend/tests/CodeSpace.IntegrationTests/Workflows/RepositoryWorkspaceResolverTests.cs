using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// <see cref="RepositoryWorkspaceResolver"/> against real Postgres + the real provider-auth layer: a
/// bound repository + credential resolve into the <see cref="WorkspaceRequest"/> the clone consumes — the
/// HTTPS URL, the default branch, a token resolved through the same auth strategies the providers use, and
/// the provider-appropriate basic-auth username. Pins: GitHub vs GitLab username; an anonymous (no-
/// credential) repo; no-RepositoryId → no workspace; an unknown / cross-team / SOFT-DELETED repo is refused.
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

    [Fact]
    public async Task A_soft_deleted_repository_is_refused()
    {
        // A repo soft-deleted AFTER its id was baked into the run (e.g. between launch validation and clone-time)
        // must not still be cloned — the clone gate filters DeletedDate just like the launch gate + every other loader.
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");
        await SoftDeleteRepoAsync(repoId);

        await Should.ThrowAsync<WorkspaceException>(async () => await ResolveAsync(TaskFor(repoId), teamId),
            customMessage: "a soft-deleted repo must be refused at the clone gate — same as an unknown repo");
    }

    [Fact]
    public async Task A_multi_repo_workspace_with_a_soft_deleted_member_is_refused()
    {
        // One member of a multi-repo workspace is soft-deleted → the whole provision fails loud (the per-repo loop
        // throws on the unresolvable repo) rather than silently dropping it from the cloned workspace.
        var teamId = await SeedTeamAsync();
        var webId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/web.git", "main", token: "ghp_web");
        var apiId = await SeedRepoAsync(teamId, ProviderKind.GitLab, "https://gitlab.com/org/api.git", "develop", token: "glpat_api");
        await SoftDeleteRepoAsync(apiId);

        var task = new AgentTask
        {
            Goal = "g", Harness = "h", Model = "m",
            Workspace = new WorkspaceSpec
            {
                PrimaryAlias = "web",
                Repositories = new[]
                {
                    new WorkspaceRepositorySpec { Alias = "web", RepositoryId = webId, Access = WorkspaceAccess.Write, IsPrimary = true },
                    new WorkspaceRepositorySpec { Alias = "api", RepositoryId = apiId, Access = WorkspaceAccess.Read },
                },
            },
        };

        await Should.ThrowAsync<WorkspaceException>(async () => await ResolveProvisionAsync(task, teamId),
            customMessage: "a soft-deleted member fails the whole multi-repo provision — never silently dropped");
    }

    [Fact]
    public async Task An_authored_single_repo_workspace_resolves_through_the_same_path_as_the_legacy_repository_id()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");

        // The canonical path the resolver now routes EVERY single-repo run through: an authored WorkspaceSpec with a
        // null per-repo ref must resolve byte-identically to the legacy RepositoryId path (default branch, token, username).
        var request = await ResolveAsync(new AgentTask { Goal = "g", Harness = "h", Model = "m", Workspace = WorkspaceSpec.FromRepository(repoId) }, teamId);

        request.ShouldNotBeNull();
        request!.RepositoryUrl.ShouldBe("https://github.com/org/repo.git");
        request.Ref.ShouldBe("main", "a null per-repo ref falls back to the repo's default branch — byte-identical to the RepositoryId path");
        request.DefaultRef.ShouldBeNull("a null per-repo ref is the default branch → no soft fallback → byte-identical");
        request.Token.ShouldBe("ghp_abc");
        request.TokenUsername.ShouldBe("x-access-token");
    }

    [Fact]
    public async Task An_authored_per_repo_ref_overrides_the_default_branch_and_stays_hard()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");

        // An AUTHORED per-repo ref (refSoftFallback NOT set) wins over the default branch AND is HARD — no soft fallback.
        var request = await ResolveAsync(new AgentTask { Goal = "g", Harness = "h", Model = "m", Workspace = WorkspaceSpec.FromRepository(repoId, "release/1.2") }, teamId);

        request!.Ref.ShouldBe("release/1.2", "the authored per-repo ref overrides the repository's default branch");
        request.DefaultRef.ShouldBeNull("an authored ref is HARD (softFallback false) — never silently rewritten; the clone fails loud if it is gone");
    }

    [Fact]
    public async Task A_session_soft_per_repo_ref_carries_the_default_branch_as_a_fallback()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");

        // A SESSION-inherited prior branch (refSoftFallback: true) is SOFT — the default branch rides along as the clone
        // fallback so a continue survives a pruned prior branch (Correction-4).
        var request = await ResolveAsync(new AgentTask { Goal = "g", Harness = "h", Model = "m", Workspace = WorkspaceSpec.FromRepository(repoId, "run-1/api", refSoftFallback: true) }, teamId);

        request!.Ref.ShouldBe("run-1/api");
        request.DefaultRef.ShouldBe("main", customMessage: "a session-soft non-default ref carries the default branch as the fallback (Correction-4)");
    }

    [Fact]
    public async Task A_requested_ref_equal_to_the_default_branch_carries_no_fallback()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");

        // A requested ref that IS the default branch needs no soft fallback (the default always exists) → DefaultRef null
        // → a hard ref → byte-identical to before (no existence pre-flight at clone time). Even marked soft, it stays null.
        var request = await ResolveAsync(new AgentTask { Goal = "g", Harness = "h", Model = "m", Workspace = WorkspaceSpec.FromRepository(repoId, "main", refSoftFallback: true) }, teamId);

        request!.Ref.ShouldBe("main");
        request.DefaultRef.ShouldBeNull("a ref equal to the default branch needs no fallback");
    }

    [Fact]
    public async Task A_direct_non_default_ref_call_stays_hard_the_acceptance_grader_path()
    {
        var teamId = await SeedTeamAsync();
        var repoId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/repo.git", "main", token: "ghp_abc");

        // The acceptance grader (+ the integrate path) call ResolveByRepositoryIdAsync DIRECTLY with the agent's PRODUCED
        // branch and NO softFallback. That ref MUST stay hard (DefaultRef null), so grading clones EXACTLY the produced
        // branch and fails loud if it is gone — never silently grading the default branch (the review's blocker guard).
        using var scope = _fixture.BeginScope();
        var request = await scope.Resolve<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(repoId, teamId, CancellationToken.None, "codespace/integration/abc123");

        request!.Ref.ShouldBe("codespace/integration/abc123");
        request.DefaultRef.ShouldBeNull("a direct non-default-ref call (the acceptance grader) is HARD — no fallback, fail loud if the produced branch is gone");
    }

    [Fact]
    public async Task A_multi_repo_workspace_resolves_every_repo_into_a_provision()
    {
        var teamId = await SeedTeamAsync();
        var webId = await SeedRepoAsync(teamId, ProviderKind.GitHub, "https://github.com/org/web.git", "main", token: "ghp_web");
        var apiId = await SeedRepoAsync(teamId, ProviderKind.GitLab, "https://gitlab.com/org/api.git", "develop", token: "glpat_api");

        var task = new AgentTask
        {
            Goal = "g", Harness = "h", Model = "m",
            Workspace = new WorkspaceSpec
            {
                PrimaryAlias = "web",
                Repositories = new[]
                {
                    new WorkspaceRepositorySpec { Alias = "web", RepositoryId = webId, Access = WorkspaceAccess.Write, IsPrimary = true },
                    new WorkspaceRepositorySpec { Alias = "api", RepositoryId = apiId, Ref = "release/2.0", Access = WorkspaceAccess.Read },
                },
            },
        };

        var provision = await ResolveProvisionAsync(task, teamId);

        provision.ShouldNotBeNull();
        provision!.Repositories.Count.ShouldBe(2, "every repo in the spec resolves into the provision (the >1 guard is lifted)");

        var web = provision.Repositories.Single(r => r.Alias == "web");
        web.CloneRequest.RepositoryUrl.ShouldBe("https://github.com/org/web.git");
        web.CloneRequest.Ref.ShouldBe("main", "null per-repo ref → the repo's default branch");
        web.CloneRequest.Token.ShouldBe("ghp_web");
        web.CloneRequest.TokenUsername.ShouldBe("x-access-token");
        web.Access.ShouldBe(WorkspaceAccess.Write);

        var api = provision.Repositories.Single(r => r.Alias == "api");
        api.CloneRequest.RepositoryUrl.ShouldBe("https://gitlab.com/org/api.git");
        api.CloneRequest.Ref.ShouldBe("release/2.0", "the per-repo ref is honoured per repository");
        api.CloneRequest.TokenUsername.ShouldBe("oauth2", "each repo resolves its own provider-appropriate auth");
        api.Access.ShouldBe(WorkspaceAccess.Read);

        provision.Primary!.Alias.ShouldBe("web");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static AgentTask TaskFor(Guid repositoryId) => new() { Goal = "g", Harness = "h", Model = "m", RepositoryId = repositoryId };

    /// <summary>Resolve and unwrap to the PRIMARY repo's clone request — so the single-repo assertions read the same WorkspaceRequest shape as before. Multi-repo resolution is asserted on the full provision in the dedicated multi-repo test.</summary>
    private async Task<WorkspaceRequest?> ResolveAsync(AgentTask task, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var provision = await scope.Resolve<IAgentWorkspaceResolver>().ResolveAsync(task, teamId, CancellationToken.None);
        return provision?.Primary?.CloneRequest;
    }

    /// <summary>Resolve to the full multi-repo provision (no unwrap) — for the multi-repo resolution test.</summary>
    private async Task<WorkspaceProvisionRequest?> ResolveProvisionAsync(AgentTask task, Guid teamId)
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

    /// <summary>Soft-delete a seeded repo (set <c>DeletedDate</c>) so the clone gate's <c>DeletedDate == null</c> filter must refuse it.</summary>
    private async Task SoftDeleteRepoAsync(Guid repoId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var repo = await db.Repository.SingleAsync(r => r.Id == repoId);
        repo.DeletedDate = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
    }
}
