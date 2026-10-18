using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// Where a credential's repositories and group hooks go when it is disconnected or its owner leaves, through the whole
/// HTTP surface: <c>POST /credentials/{id}/revoke</c> with and without a body, <c>GET /credentials/{id}/dependents</c> and
/// <c>/relink-suggestions</c>, and <c>POST /credentials/{id}/relink-bindings</c>.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the real ASP.NET pipeline (routing, JWT auth, the <c>X-Team-Id</c> scope, model
/// binding including the optional revoke body, the controllers, the exception filter, the mediator and its authorization
/// behaviors), real Postgres, and the real job drain: a hook revived by a move is registered again by the production
/// registrar. One documented seam — the provider is the Git test provider (whoami, catalog, hook registration), so
/// nothing real is contacted. "A run on the repository succeeds" is the run's own first read, the production
/// <see cref="IAgentWorkspaceResolver"/> on the host's container, which presents the credential the repository is bound
/// to — and refuses a revoked one.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class CredentialBindingsHttpE2ETests : IClassFixture<CredentialBindingsHttpE2ETests.GitProviderApiFactory>
{
    private readonly GitProviderApiFactory _factory;
    private readonly TenantHarness _tenants;

    public CredentialBindingsHttpE2ETests(GitProviderApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task A_disconnect_naming_the_successor_moves_the_repository_and_the_next_run_clones_through_it()
    {
        var world = await SeedWorldAsync();
        var dying = await SeedCredentialAsync(world, world.OwnerId, "pat-dying");
        var successor = await SeedCredentialAsync(world, world.OwnerId, "pat-successor");
        var spare = await SeedCredentialAsync(world, world.OwnerId, "pat-spare");
        var repoId = await SeedRepositoryAsync(world, dying);

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dying}/revoke", world.OwnerId, world.TeamId, new { moveBindingsToCredentialId = successor });

        var body = await ReadJsonAsync(response);

        body.GetProperty("movedRepositoryCount").GetInt32().ShouldBe(1);
        body.GetProperty("movedToCredentialId").GetGuid().ShouldBe(successor, "two other connections were live, so only the named one could be the destination");
        body.GetProperty("affectedRepositoryCount").GetInt32().ShouldBe(0);

        var repo = await LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(successor);
        repo.Status.ShouldBe(RepositoryStatus.Active);
        (await ResolveCloneTokenAsync(world, repoId)).ShouldBe("pat-successor", "the run's first read presents the successor's token");
        (await LoadCredentialAsync(spare)).Status.ShouldBe(CredentialStatus.Active);
        (await LoadCredentialAsync(dying)).Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task A_disconnect_with_no_body_behaves_as_it_always_did_unless_the_owner_has_exactly_one_other_connection(int others, bool moves)
    {
        // The Providers modal sends no body today: POST with nothing. That is the request that stranded repositories, and
        // the one an unchanged client keeps sending.
        var world = await SeedWorldAsync();
        var dying = await SeedCredentialAsync(world, world.OwnerId, "pat-dying");
        var survivors = new List<Guid>();

        for (var i = 0; i < others; i++) survivors.Add(await SeedCredentialAsync(world, world.OwnerId, $"pat-other-{i}"));

        var repoId = await SeedRepositoryAsync(world, dying);

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dying}/revoke", world.OwnerId, world.TeamId);

        var body = await ReadJsonAsync(response);
        var repo = await LoadRepositoryAsync(repoId);

        body.GetProperty("movedRepositoryCount").GetInt32().ShouldBe(moves ? 1 : 0);
        body.GetProperty("affectedRepositoryCount").GetInt32().ShouldBe(moves ? 0 : 1);
        repo.CredentialId.ShouldBe(moves ? survivors[0] : dying);
        repo.Status.ShouldBe(moves ? RepositoryStatus.Active : RepositoryStatus.Error);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task The_usage_preview_says_what_the_disconnect_with_no_body_then_does(int others, bool moves)
    {
        // The confirm dialog quotes GET /usage and then POSTs the disconnect with no body: what the first says must be what the
        // second does, or the dialog warns about a break that will not happen — or promises a move that will not.
        var world = await SeedWorldAsync();
        var dying = await SeedCredentialAsync(world, world.OwnerId, "pat-dying");
        var survivors = new List<Guid>();

        for (var i = 0; i < others; i++) survivors.Add(await SeedCredentialAsync(world, world.OwnerId, $"pat-other-{i}"));

        await SeedRepositoryAsync(world, dying);
        await SeedRepositoryAsync(world, dying);

        var usage = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{dying}/usage", world.OwnerId, world.TeamId));
        var body = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dying}/revoke", world.OwnerId, world.TeamId));

        usage.GetProperty("activeRepositoryCount").GetInt32().ShouldBe(moves ? 0 : 2);
        usage.GetProperty("willMoveRepositoryCount").GetInt32().ShouldBe(moves ? 2 : 0);
        (usage.GetProperty("willMoveToDisplayName").GetString() is { } named ? named : null).ShouldBe(moves ? "pat-other-0" : null);

        body.GetProperty("affectedRepositoryCount").GetInt32().ShouldBe(usage.GetProperty("activeRepositoryCount").GetInt32());
        body.GetProperty("movedRepositoryCount").GetInt32().ShouldBe(usage.GetProperty("willMoveRepositoryCount").GetInt32());
    }

    [Fact]
    public async Task An_admin_disconnecting_a_teammates_credential_with_no_body_moves_nothing_and_the_preview_says_so()
    {
        var world = await SeedWorldAsync();
        var theirs = await SeedCredentialAsync(world, world.MemberId, "pat-member-first");
        var theirsOther = await SeedCredentialAsync(world, world.MemberId, "pat-member-second");
        var repoId = await SeedRepositoryAsync(world, theirs);

        var usage = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{theirs}/usage", world.AdminId, world.TeamId));
        var body = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{theirs}/revoke", world.AdminId, world.TeamId));
        var repo = await LoadRepositoryAsync(repoId);

        usage.GetProperty("activeRepositoryCount").GetInt32().ShouldBe(1);
        usage.GetProperty("willMoveRepositoryCount").GetInt32().ShouldBe(0);
        body.GetProperty("movedRepositoryCount").GetInt32().ShouldBe(0, "the teammate's other connection is the teammate's to choose: an Admin names it, or nothing moves");
        repo.CredentialId.ShouldBe(theirs);
        repo.Status.ShouldBe(RepositoryStatus.Error);
        (await LoadCredentialAsync(theirsOther)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task For_a_live_credential_the_suggestions_are_the_owners_other_connections_which_the_disconnect_accepts()
    {
        var world = await SeedWorldAsync();
        var theirs = await SeedCredentialAsync(world, world.MemberId, "pat-member-first");
        var theirsOther = await SeedCredentialAsync(world, world.MemberId, "pat-member-second");
        await SeedCredentialAsync(world, world.AdminId, "pat-admin");
        await SeedCredentialAsync(world, ownerId: null, "pat-team", CredentialOwnership.TeamService);
        var repoId = await SeedRepositoryAsync(world, theirs);

        var suggestions = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{theirs}/relink-suggestions", world.AdminId, world.TeamId));

        suggestions.EnumerateArray().Select(s => s.GetProperty("credentialId").GetGuid()).ShouldBe(new[] { theirsOther }, "not the Admin's own connection, and not the team's token: a Disconnect would refuse both");
        suggestions[0].GetProperty("kind").ToString().ShouldBe("OwnersOtherConnection");

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{theirs}/revoke", world.AdminId, world.TeamId, new { moveBindingsToCredentialId = theirsOther });

        (await ReadJsonAsync(response)).GetProperty("movedRepositoryCount").GetInt32().ShouldBe(1);
        (await LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(theirsOther);
    }

    [Fact]
    public async Task The_route_decides_which_credential_is_revoked_whatever_the_body_says()
    {
        var world = await SeedWorldAsync();
        var named = await SeedCredentialAsync(world, world.OwnerId, "pat-named-in-route");
        var other = await SeedCredentialAsync(world, world.OwnerId, "pat-named-in-body");

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{named}/revoke", world.OwnerId, world.TeamId, new { credentialId = other });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
        (await LoadCredentialAsync(named)).Status.ShouldBe(CredentialStatus.Revoked);
        (await LoadCredentialAsync(other)).Status.ShouldBe(CredentialStatus.Active, "a body that disagrees with the URL cannot redirect the revoke");
    }

    [Fact]
    public async Task A_refused_target_is_a_400_that_says_why_and_the_credential_stays_connected()
    {
        var world = await SeedWorldAsync();
        var dying = await SeedCredentialAsync(world, world.OwnerId, "pat-dying");
        var teammates = await SeedCredentialAsync(world, world.AdminId, "pat-teammates");

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dying}/revoke", world.OwnerId, world.TeamId, new { moveBindingsToCredentialId = teammates });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(response));
        (await ReadJsonAsync(response, requireSuccess: false)).GetProperty("message").GetString().ShouldContain("belongs to someone else");
        (await LoadCredentialAsync(dying)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task When_an_owner_leaves_an_admin_sees_what_waits_picks_a_home_and_relinks_it_all_in_one_call()
    {
        var world = await SeedWorldAsync();
        var leaver = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, leaver, TeamRole.Member);
        var theirs = await SeedCredentialAsync(world, leaver, "pat-leaver");
        var first = await SeedRepositoryAsync(world, theirs);
        var second = await SeedRepositoryAsync(world, theirs);
        var hookId = await SeedHookAsync(world, theirs, RepositoryWebhookRegistrationStatus.DeadLettered);
        var adminsOwn = await SeedCredentialAsync(world, world.AdminId, "pat-admin");
        var teamToken = await SeedCredentialAsync(world, ownerId: null, "pat-team", CredentialOwnership.TeamService);
        await SeedCredentialAsync(world, world.OwnerId, "pat-owners-personal");

        var removal = await _tenants.SendAsync(HttpMethod.Delete, $"/api/teams/members/{leaver}", world.OwnerId, world.TeamId);
        removal.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(removal));

        var listed = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", world.AdminId, world.TeamId));
        listed.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldNotContain(theirs, "fixture check: the retired token is out of the credential list, so a UI built on it could not even name it");

        // What is waiting — asked of the retired credential, by a Viewer-level read (membership is read access).
        var viewer = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, viewer, TeamRole.Viewer);
        var dependents = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{theirs}/dependents", viewer, world.TeamId));

        dependents.GetProperty("retired").GetBoolean().ShouldBeTrue();
        dependents.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe(new[] { first, second }, ignoreOrder: true);
        dependents.GetProperty("connectionWebhooks").GetArrayLength().ShouldBe(1);

        // Where it could go — the caller's own connection, then the team's token; never the Owner's personal one.
        var suggestions = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{theirs}/relink-suggestions", world.AdminId, world.TeamId));
        suggestions.EnumerateArray().Select(s => s.GetProperty("credentialId").GetGuid()).ShouldBe(new[] { adminsOwn, teamToken });
        suggestions[0].GetProperty("kind").ToString().ShouldBe("YourConnection");

        // One call.
        var relink = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{theirs}/relink-bindings", world.AdminId, world.TeamId, new { toCredentialId = adminsOwn });
        var result = await ReadJsonAsync(relink);

        result.GetProperty("movedRepositoryCount").GetInt32().ShouldBe(2);
        result.GetProperty("movedConnectionWebhookCount").GetInt32().ShouldBe(1);

        foreach (var repoId in new[] { first, second })
        {
            (await LoadRepositoryAsync(repoId)).Status.ShouldBe(RepositoryStatus.Active);
            (await ResolveCloneTokenAsync(world, repoId)).ShouldBe("pat-admin", "a run on either repository clones through the Admin's connection");
        }

        // Hooks revive: the move handed the buried hook to the registrar, and the registrar — running through the hook's NEW
        // credential — registers it.
        await _factory.JobClient.DrainAsync();

        var hook = await LoadHookAsync(hookId);

        hook.CredentialId.ShouldBe(adminsOwn);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, "a dead-lettered group hook that gained a live credential registers again");

        var after = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{theirs}/dependents", viewer, world.TeamId));
        after.GetProperty("repositories").GetArrayLength().ShouldBe(0, "nothing is left waiting on the retired credential");
        after.GetProperty("connectionWebhooks").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData(TeamRole.Viewer)]
    [InlineData(TeamRole.Member)]
    public async Task Below_admin_the_bulk_relink_is_forbidden_but_the_reads_are_not(TeamRole role)
    {
        var world = await SeedWorldAsync();
        var caller = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, caller, role);
        var dead = await SeedCredentialAsync(world, world.MemberId, "pat-dead", status: CredentialStatus.Revoked);
        var target = await SeedCredentialAsync(world, caller, "pat-callers");
        var repoId = await SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var relink = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dead}/relink-bindings", caller, world.TeamId, new { toCredentialId = target });
        var dependents = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{dead}/dependents", caller, world.TeamId);
        var suggestions = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{dead}/relink-suggestions", caller, world.TeamId);

        relink.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(relink));
        dependents.StatusCode.ShouldBe(HttpStatusCode.OK, "membership is read access");
        suggestions.StatusCode.ShouldBe(HttpStatusCode.OK, "membership is read access");
        (await LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task Another_teams_admin_reaches_none_of_it_and_a_foreign_credential_reads_as_not_found()
    {
        var world = await SeedWorldAsync();
        var outsider = await _tenants.AccountAsync();
        var outsiderTeam = await _tenants.TeamAsync((outsider, TeamRole.Owner));
        var dead = await SeedCredentialAsync(world, world.MemberId, "pat-dead", status: CredentialStatus.Revoked);
        var target = await SeedCredentialAsync(world, world.AdminId, "pat-admin");
        var repoId = await SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var asOutsiderToThisTeam = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dead}/relink-bindings", outsider, world.TeamId, new { toCredentialId = target });
        var asOutsiderInTheirTeam = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dead}/relink-bindings", outsider, outsiderTeam, new { toCredentialId = target });
        var dependentsInTheirTeam = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{dead}/dependents", outsider, outsiderTeam);

        asOutsiderToThisTeam.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(asOutsiderToThisTeam));
        asOutsiderInTheirTeam.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a credential of another team reads as not found, so it cannot be told from one that does not exist");
        dependentsInTheirTeam.StatusCode.ShouldBe(HttpStatusCode.NotFound, await TenantHarness.DescribeAsync(dependentsInTheirTeam));
        (await LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task A_relink_onto_a_teammates_personal_connection_is_a_400_that_says_whose_it_is()
    {
        var world = await SeedWorldAsync();
        var dead = await SeedCredentialAsync(world, world.MemberId, "pat-dead", status: CredentialStatus.Revoked);
        var teammates = await SeedCredentialAsync(world, world.OwnerId, "pat-owners");
        await SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dead}/relink-bindings", world.AdminId, world.TeamId, new { toCredentialId = teammates });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(response));
        (await ReadJsonAsync(response, requireSuccess: false)).GetProperty("message").GetString().ShouldContain("another person's personal connection");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid AdminId, Guid MemberId, Guid InstanceId);

    private async Task<World> SeedWorldAsync()
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var member = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin), (member, TeamRole.Member));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = $"https://gitlab.bindings.test/{suffix}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return new World(teamId, owner, admin, member, instance.Id);
    }

    private async Task<Guid> SeedCredentialAsync(World world, Guid? ownerId, string token, CredentialOwnership? ownership = null, CredentialStatus status = CredentialStatus.Active)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var sealedToken = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token }));

        var credential = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, OwnerUserId = ownerId, Ownership = ownership ?? (ownerId == null ? CredentialOwnership.TeamService : CredentialOwnership.Personal), AuthType = AuthType.Pat, DisplayName = token, EncryptedPayload = status == CredentialStatus.Revoked ? string.Empty : sealedToken, Status = status, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }

    private async Task<Guid> SeedRepositoryAsync(World world, Guid credentialId, RepositoryStatus status = RepositoryStatus.Active)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, ExternalId = $"acme/ext-{suffix}", NamespacePath = "acme", Name = $"app-{suffix}", FullPath = $"acme/app-{suffix}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://gitlab.bindings.test", CloneUrlHttps = $"https://gitlab.bindings.test/acme/app-{suffix}.git", Status = status, LastError = status == RepositoryStatus.Error ? "Credential disconnected." : null, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        return repository.Id;
    }

    private async Task<Guid> SeedHookAsync(World world, Guid credentialId, RepositoryWebhookRegistrationStatus status)
    {
        var id = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = id, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, OwnerPath = "acme", CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}", SecretEnc = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt("hook-secret"), SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status, Attempts = 3, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), LastError = "401 Unauthorized", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();

        return id;
    }

    private Task<Repository> LoadRepositoryAsync(Guid id) => _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == id));

    private Task<Credential> LoadCredentialAsync(Guid id) => _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<ConnectionWebhook> LoadHookAsync(Guid id) => _tenants.DbAsync(db => db.ConnectionWebhook.AsNoTracking().SingleAsync(h => h.Id == id));

    /// <summary>What every repository-bound run does first, on the host's own container: the production resolver, which presents the bound credential and refuses a revoked one.</summary>
    private async Task<string?> ResolveCloneTokenAsync(World world, Guid repositoryId)
    {
        using var scope = _factory.Services.CreateScope();

        var request = await scope.ServiceProvider.GetRequiredService<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(repositoryId, world.TeamId, CancellationToken.None);

        return request!.Token;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, bool requireSuccess = true)
    {
        if (requireSuccess) response.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    /// <summary>
    /// The app with one seam: the repositories live on a GitLab instance, so the production GitLab auth strategies read every
    /// credential, and the Git test provider answers what GitLab's API would — whoami, catalog, hook registration.
    /// </summary>
    public sealed class GitProviderApiFactory : TaskLaunchApiFactory
    {
        protected override void ConfigureContractTestServices(ContainerBuilder builder)
        {
            builder.RegisterType<TestRemoteHookStore>().AsSelf().SingleInstance();
            builder.RegisterType<TestPullRequestOpenCapture>().AsSelf().SingleInstance();
            builder.RegisterType<TestRepositoryProvider>().AsImplementedInterfaces().InstancePerLifetimeScope();
            builder.Register<IProviderRegistry>(c => new GitStandsInForGitLabRegistry(new ProviderRegistry(c.Resolve<IEnumerable<IProviderCapability>>()))).InstancePerLifetimeScope();
        }
    }

    /// <summary>Answers every capability lookup with the Git test provider's, so reading a repository or registering a hook contacts nothing real.</summary>
    private sealed class GitStandsInForGitLabRegistry : IProviderRegistry
    {
        private readonly IProviderRegistry _inner;

        public GitStandsInForGitLabRegistry(IProviderRegistry inner) { _inner = inner; }

        public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability => _inner.Require<TCapability>(ProviderKind.Git);

        public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability => _inner.TryGet(ProviderKind.Git, out capability);

        public IReadOnlyList<Type> GetCapabilities(ProviderKind kind) => _inner.GetCapabilities(ProviderKind.Git);
    }
}
