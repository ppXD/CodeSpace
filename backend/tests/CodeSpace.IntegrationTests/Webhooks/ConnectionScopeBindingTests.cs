using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// What the scope setting is FOR: under connection-wide scope binding a repository must register no
/// per-repository hook, and switching an existing connection must never leave both modes delivering.
/// Two hooks for one event is not a cosmetic duplicate — it starts the workflow twice, which posts
/// the review twice and opens the pull request twice.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConnectionScopeBindingTests
{
    private readonly PostgresFixture _fixture;

    public ConnectionScopeBindingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        using var scope = _fixture.BeginScope();
        scope.Resolve<TestRemoteHookStore>().Reset();
    }

    [Fact]
    public async Task Binding_under_connection_scope_registers_the_group_hook_and_no_repository_hook()
    {
        var seed = await SeedAsync(ProviderWebhookScope.Connection).ConfigureAwait(false);

        var repositoryId = await BindAsync(seed, $"acme/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.RepositoryWebhook.AsNoTracking().AnyAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "A connection-wide connection must not stage a per-repository hook — the group hook already covers this repository, so a second one would deliver every event twice.");

        var hook = await db.ConnectionWebhook.AsNoTracking().SingleAsync(w => w.ProviderInstanceId == seed.InstanceId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered,
            customMessage: $"The group hook should have registered. last_error={hook.LastError}");
        hook.OwnerPath.ShouldBe("acme");

        verify.Resolve<TestRemoteHookStore>().ConnectionOwnerPaths.ShouldBe(new[] { "acme" },
            customMessage: "The hook has to land on the group that contains the project; a hook registered elsewhere would look registered and deliver nothing.");
    }

    [Fact]
    public async Task A_second_repository_in_the_same_group_reuses_the_one_hook()
    {
        var seed = await SeedAsync(ProviderWebhookScope.Connection).ConfigureAwait(false);

        await BindAsync(seed, $"acme/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        await BindAsync(seed, $"acme/web-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();

        var hooks = await verify.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking()
            .Where(w => w.ProviderInstanceId == seed.InstanceId).ToListAsync().ConfigureAwait(false);

        hooks.Count.ShouldBe(1,
            customMessage: "One hook covers the whole group — a second repository under it needs nothing registered, and a second hook would double every delivery.");
        verify.Resolve<TestRemoteHookStore>().ConnectionOwnerPaths.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Binding_under_the_default_scope_still_registers_a_repository_hook()
    {
        // The mode every existing connection is in. It must be untouched by the new one.
        var seed = await SeedAsync(ProviderWebhookScope.Repository).ConfigureAwait(false);

        var repositoryId = await BindAsync(seed, $"acme/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);

        (await db.ConnectionWebhook.AsNoTracking().AnyAsync(w => w.ProviderInstanceId == seed.InstanceId).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task Switching_to_connection_scope_retires_the_repository_hooks_it_replaces()
    {
        var seed = await SeedAsync(ProviderWebhookScope.Repository).ConfigureAwait(false);

        var repositoryId = await BindAsync(seed, $"acme/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        await SetScopeAsync(seed, ProviderWebhookScope.Connection).ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var repositoryHooks = await db.RepositoryWebhook.AsNoTracking().Where(w => w.RepositoryId == repositoryId).ToListAsync().ConfigureAwait(false);
        repositoryHooks.ShouldAllBe(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Cancelled,
            customMessage: "Every per-repository hook must be out of service after the switch — leaving one live means both modes deliver, and one push starts two runs.");

        var connectionHook = await db.ConnectionWebhook.AsNoTracking().SingleAsync(w => w.ProviderInstanceId == seed.InstanceId).ConfigureAwait(false);
        connectionHook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered,
            customMessage: $"The incoming mode has to be registered or the switch silently turns the connection off. last_error={connectionHook.LastError}");
    }

    [Fact]
    public async Task Switching_back_to_repository_scope_retires_the_group_hook_and_restores_the_repository_ones()
    {
        var seed = await SeedAsync(ProviderWebhookScope.Connection).ConfigureAwait(false);

        var repositoryId = await BindAsync(seed, $"acme/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        await SetScopeAsync(seed, ProviderWebhookScope.Repository).ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.ConnectionWebhook.AsNoTracking().AnyAsync(w => w.ProviderInstanceId == seed.InstanceId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered).ConfigureAwait(false))
            .ShouldBeFalse(customMessage: "The group hook must stop before the per-repository ones start.");

        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered,
            customMessage: $"Every bound repository has to end up with the hook it would have had if the connection had never left this mode. last_error={hook.LastError}");
    }

    [Fact]
    public async Task A_subgroup_is_covered_by_the_group_hook_above_it()
    {
        // The owner is the remote's namespace verbatim, so `acme/platform` and `acme/platform/web`
        // are two different owners and would each take a hook. GitLab's group hooks cover subgroups
        // ("events across all projects in a group and its subgroups"), so both would then fire for a
        // push in the subgroup — two verified deliveries, two workflow runs, two reviews posted.
        var seed = await SeedAsync(ProviderWebhookScope.Connection).ConfigureAwait(false);

        await BindAsync(seed, $"acme/platform/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        await BindAsync(seed, $"acme/platform/web/ui-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        var hooks = await LoadHooksAsync(seed.InstanceId).ConfigureAwait(false);

        hooks.Count.ShouldBe(1,
            customMessage: "An ancestor's hook already covers the subgroup — a second hook is not redundancy, it is every push in the subgroup starting two runs.");
        hooks[0].OwnerPath.ShouldBe("acme/platform");
    }

    [Fact]
    public async Task Registering_above_an_existing_hook_retires_the_one_it_swallows()
    {
        // The reverse ordering, which the ancestor rule alone does not handle: the new hook is the
        // ANCESTOR, so nothing above it exists to stop it — and the narrower hook it now covers has
        // to go, or the same push arrives on both.
        var seed = await SeedAsync(ProviderWebhookScope.Connection).ConfigureAwait(false);

        await BindAsync(seed, $"acme/platform/api-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        await BindAsync(seed, $"acme/cli-{Guid.NewGuid():N}").ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);

        var hooks = await LoadHooksAsync(seed.InstanceId).ConfigureAwait(false);

        hooks.ShouldContain(h => h.OwnerPath == "acme" && h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered,
            customMessage: "The hook on the ancestor is the one that should end up live.");
        hooks.ShouldNotContain(h => h.OwnerPath == "acme/platform" && h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered,
            customMessage: "A hook the new one now covers must be retired — leaving it live means the subgroup's pushes arrive twice.");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<List<ConnectionWebhook>> LoadHooksAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking()
            .Where(w => w.ProviderInstanceId == instanceId)
            .ToListAsync().ConfigureAwait(false);
    }


    private async Task<Guid> BindAsync(Seed seed, string projectIdentifier)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), seed.TeamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new BindRepositoryCommand
        {
            ProviderInstanceId = seed.InstanceId,
            CredentialId = seed.CredentialId,
            ProjectIdentifier = projectIdentifier
        }).ConfigureAwait(false);
    }

    private async Task SetScopeAsync(Seed seed, ProviderWebhookScope scope)
    {
        using var actingScope = _fixture.BeginScopeAs(Guid.NewGuid(), seed.TeamId, Roles.Admin);

        await actingScope.Resolve<IMediator>().Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = seed.InstanceId,
            WebhookScope = scope
        }).ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<InMemoryBackgroundJobClient>().WaitForPendingAsync().ConfigureAwait(false);
    }

    private async Task<Seed> SeedAsync(ProviderWebhookScope webhookScope)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, owner.Id);
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.Git,
            DisplayName = "Test",
            BaseUrl = $"https://test-{suffix}.local",
            WebhookScope = webhookScope
        };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.Project.Add(project);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Seed { TeamId = team.Id, InstanceId = instance.Id, CredentialId = credential.Id };
    }

    private sealed record Seed
    {
        public required Guid TeamId { get; init; }
        public required Guid InstanceId { get; init; }
        public required Guid CredentialId { get; init; }
    }
}
