using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Defaults;

/// <summary>
/// The materializer against a real database and the real storage services — the only place its ordering constraints
/// exist. Every write it makes is undeletable once committed, so most of what these tests assert is what is NOT there
/// after a refusal.
/// </summary>
[Collection(DeploymentDefaultsCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageDefaultMaterializerFlowTests : IDisposable
{
    private const string ProviderTypeKey = "local-rwx/v1";
    private const string AgentRunLogClass = "agent-run-log/v1";

    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = new();

    public StorageDefaultMaterializerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_enabled_template_gives_the_team_an_active_route_pinned_to_the_revision_it_proved()
    {
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot());

        var outcome = await MaterializeAsync(world, automatic: true);

        var materialized = outcome.ShouldBeOfType<StorageMaterialization.Materialized>();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var profile = await db.StorageProfile.AsNoTracking().SingleAsync(row => row.Id == materialized.StorageProfileId);
        profile.State.ShouldBe(StorageProfileState.Active, "a route may only name an Active profile");

        var route = await db.StorageRoute.Include(row => row.Revisions).AsNoTracking().SingleAsync(row => row.Id == materialized.StorageRouteId);
        route.State.ShouldBe(StorageRouteState.Active);
        route.DataClassTypeKey.ShouldBe(AgentRunLogClass);

        var revision = route.Revisions.Single(row => row.Revision == route.CurrentRevision);
        revision.PinnedProfileRevision.ShouldBe(profile.CurrentRevision,
            "the route must pin the exact revision the probe proved, not follow whatever the profile becomes later");

        var provenance = await db.StorageDefaultMaterialization.AsNoTracking()
            .SingleAsync(row => row.TeamId == world.TeamId && row.DataClassTypeKey == AgentRunLogClass);
        provenance.StorageProfileId.ShouldBe(materialized.StorageProfileId);
        provenance.SourceRevision.ShouldBe(materialized.SourceRevision);
    }

    [Fact]
    public async Task Two_teams_materialized_from_one_template_get_different_namespaces()
    {
        // The reason the whole tier exists. Object keys carry no team segment, so identical content in two teams is
        // one physical object unless their namespaces differ — and a per-team purge deletes by an ETag identical
        // bytes share. One shared namespace means one team's reaper deletes the other's live artifacts.
        var first = await SeedTeamAsync();
        var second = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot());

        var one = (await MaterializeAsync(first, automatic: true)).ShouldBeOfType<StorageMaterialization.Materialized>();
        var two = (await MaterializeAsync(second, automatic: true)).ShouldBeOfType<StorageMaterialization.Materialized>();

        var fingerprints = await FingerprintsAsync(one.StorageProfileId, two.StorageProfileId);

        fingerprints.Distinct().Count().ShouldBe(2, "two teams sharing one namespace fingerprint share one physical object per identical payload");
    }

    [Fact]
    public async Task Materializing_twice_is_idempotent_and_creates_nothing_the_second_time()
    {
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot());

        var first = (await MaterializeAsync(world, automatic: true)).ShouldBeOfType<StorageMaterialization.Materialized>();
        var second = await MaterializeAsync(world, automatic: true);

        var already = second.ShouldBeOfType<StorageMaterialization.AlreadyMaterialized>();
        already.StorageProfileId.ShouldBe(first.StorageProfileId, "the second call must name the profile the first one made, not a new one");

        (await CountAsync(world.TeamId)).ShouldBe(new Counts(Profiles: 1, Routes: 1, Provenance: 1),
            "a second materialization that created anything would leave a row nothing can delete");
    }

    [Fact]
    public async Task A_team_that_configured_its_own_route_is_left_alone()
    {
        // A default is a default. The team's own row always wins, and the deployment never displaces it — matching the
        // shipped agent-run-log bootstrap, which treats an existing route in ANY lifecycle state as authoritative.
        var world = await SeedTeamAsync();
        var ownRoute = await SeedOwnRouteAsync(world);
        await AuthorTemplateAsync(NewRoot());

        var outcome = await MaterializeAsync(world, automatic: true);

        outcome.ShouldBeOfType<StorageMaterialization.TeamOwnsRoute>().StorageRouteId.ShouldBe(ownRoute);
        (await CountAsync(world.TeamId)).ShouldBe(new Counts(Profiles: 1, Routes: 1, Provenance: 0), "only the team's own profile and route may exist");
    }

    [Fact]
    public async Task A_disabled_template_materializes_nothing()
    {
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot(), enabled: false);

        (await MaterializeAsync(world, automatic: true)).ShouldBeOfType<StorageMaterialization.TemplateDisabled>();
        (await CountAsync(world.TeamId)).ShouldBe(new Counts(0, 0, 0));
    }

    [Fact]
    public async Task A_template_disabled_by_an_uncommitted_operator_edit_blocks_the_materializer_rather_than_racing_it()
    {
        // SetEnabledAsync deliberately does NOT advance the template revision, so a materializer that re-checked by
        // comparing revisions would see an unchanged number and activate an irreversible route from a template the
        // operator had already switched off. The row lock is what closes that window, so this asserts the lock and not
        // the comparison: while an operator's UPDATE is open, the materializer must not get past its read.
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot());

        using var holder = _fixture.BeginScope();
        var holderDb = holder.Resolve<CodeSpaceDbContext>();
        await using var operatorEdit = await holderDb.Database.BeginTransactionAsync();
        await holderDb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE storage_default SET is_enabled = FALSE WHERE data_class_type_key = {AgentRunLogClass}");

        var blocked = MaterializeAsync(world, automatic: true);
        var raced = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(2)));

        raced.ShouldNotBe(blocked, "the materializer read the template while an operator's disable was still open, so it could act on a value that is about to stop being true");

        await operatorEdit.CommitAsync();

        (await blocked.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeOfType<StorageMaterialization.TemplateDisabled>(
            "once the operator's disable commits, the materializer must observe it rather than the value it first read");
        (await CountAsync(world.TeamId)).ShouldBe(new Counts(0, 0, 0));
    }

    [Theory]
    [InlineData(StorageDefaultAdoptionPolicyValue.Explicit, true, false)]   // a first write may not take a team off local storage
    [InlineData(StorageDefaultAdoptionPolicyValue.Explicit, false, true)]   // a team admin choosing it may
    [InlineData(StorageDefaultAdoptionPolicyValue.Automatic, true, true)]   // a class with no local home has nothing to lose
    public async Task Adoption_policy_decides_which_caller_may_materialize(StorageDefaultAdoptionPolicyValue policy, bool automatic, bool expectMaterialized)
    {
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot(), policy: policy);

        var outcome = await MaterializeAsync(world, automatic);

        if (expectMaterialized) outcome.ShouldBeOfType<StorageMaterialization.Materialized>();
        else
        {
            outcome.ShouldBeOfType<StorageMaterialization.AdoptionRequiresChoice>();
            (await CountAsync(world.TeamId)).ShouldBe(new Counts(0, 0, 0));
        }
    }

    [Fact]
    public async Task A_destination_that_cannot_be_written_leaves_nothing_behind()
    {
        // The step the transaction exists for. Activating the route makes this data class fail CLOSED — there is no
        // fallback to local — so a destination that rejects writes does not degrade the team, it fails runs that would
        // have succeeded. And none of the rows a half-materialization leaves can ever be deleted: storage_profile and
        // storage_credential both reject DELETE, and a route that reached Active can never return to Draft.
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync("/dev/null/codespace-cannot-write-here");

        var outcome = await MaterializeAsync(world, automatic: true);

        outcome.ShouldBeOfType<StorageMaterialization.DestinationUnusable>().Reason.ShouldNotBeNullOrWhiteSpace(
            "the operator has to be told which of the credential and the namespace to fix");
        (await CountAsync(world.TeamId)).ShouldBe(new Counts(0, 0, 0),
            "a profile or credential left behind by a failed materialization is permanent — neither can be deleted");
    }

    [Fact]
    public async Task Two_concurrent_materializations_of_one_team_produce_exactly_one()
    {
        // The deployment is multi-worker and multi-node, so this is an ordinary event. Only one of the two route
        // inserts can win ux_storage_route_team_data_class, and the loser has already committed a profile and a
        // credential by the time it finds out.
        var world = await SeedTeamAsync();
        await AuthorTemplateAsync(NewRoot());

        var outcomes = await Task.WhenAll(MaterializeAsync(world, automatic: true), MaterializeAsync(world, automatic: true));

        outcomes.OfType<StorageMaterialization.Materialized>().Count().ShouldBe(1, "exactly one caller may create the team's route");
        outcomes.Count(outcome => outcome is StorageMaterialization.AlreadyMaterialized or StorageMaterialization.TeamOwnsRoute).ShouldBe(1,
            "the loser must observe the winner's committed outcome rather than fail");

        (await CountAsync(world.TeamId)).ShouldBe(new Counts(Profiles: 1, Routes: 1, Provenance: 1),
            "a second profile or credential from the losing caller would be permanent");
    }

    [Fact]
    public async Task A_class_with_no_template_materializes_nothing()
    {
        // Asked about a class this suite never authors, because a template is deployment scope and is NEVER deleted:
        // once any other test in this collection has authored one for the shared class, "no template" is no longer a
        // reachable state for it. The key is well-formed, so it reaches the template read rather than being rejected
        // earlier for its shape.
        var world = await SeedTeamAsync();

        (await MaterializeAsync(world, automatic: true, dataClass: "never-authored/v1")).ShouldBeOfType<StorageMaterialization.NoTemplate>();
        (await CountAsync(world.TeamId)).ShouldBe(new Counts(0, 0, 0));
    }

    [Fact]
    public async Task An_unknown_team_is_refused_by_name_rather_than_read_as_a_missing_template()
    {
        await AuthorTemplateAsync(NewRoot());

        var outcome = await MaterializeAsync(new World(Guid.NewGuid(), Guid.NewGuid()), automatic: true);

        outcome.ShouldBeOfType<StorageMaterialization.TeamNotFound>("authoring a template and fixing the caller are opposite responses");
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<StorageMaterialization> MaterializeAsync(World world, bool automatic, string? dataClass = null)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IStorageDefaultMaterializer>()
            .MaterializeAsync(new StorageMaterializationRequest(world.TeamId, dataClass ?? AgentRunLogClass, world.ActorId, automatic), CancellationToken.None);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-materializer", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    /// <summary>
    /// Authors the deployment template through its own service, so its admission rules apply here exactly as they do to
    /// an operator.
    ///
    /// <para>Create-or-update rather than create, because a template is DEPLOYMENT scope: there is one row per data
    /// class for the whole instance, it is never deleted, and this suite shares one database with everything else in
    /// its collection. A second test that only ever created would collide with the first test's row — and a suite that
    /// worked around that by giving each test its own data class key would stop testing the real one.</para>
    /// </summary>
    private async Task AuthorTemplateAsync(string namespaceRoot, bool enabled = true, StorageDefaultAdoptionPolicyValue policy = StorageDefaultAdoptionPolicyValue.Automatic)
    {
        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IStorageDefaultService>();
        var actorId = await SeedOperatorAsync(scope);
        var existing = (await service.ListAsync(CancellationToken.None)).SingleOrDefault(row => row.DataClassTypeKey == AgentRunLogClass);

        if (existing == null)
        {
            await service.CreateAsync(actorId, new CreateStorageDefaultCommand
            {
                DataClassTypeKey = AgentRunLogClass,
                ProviderTypeKey = ProviderTypeKey,
                NonSecretConfig = JsonDocument.Parse("{}").RootElement,
                NamespaceRoot = namespaceRoot,
                AdoptionPolicy = policy,
                IsEnabled = enabled,
            }, CancellationToken.None);
            return;
        }

        var updated = await service.UpdateAsync(actorId, new UpdateStorageDefaultCommand
        {
            DefaultId = existing.Id,
            ExpectedXmin = existing.Xmin,
            ExpectedRevision = existing.Revision,
            ProviderTypeKey = ProviderTypeKey,
            NonSecretConfig = JsonDocument.Parse("{}").RootElement,
            NamespaceRoot = namespaceRoot,
            AdoptionPolicy = policy,
        }, CancellationToken.None);

        var current = updated.ShouldNotBeNull();
        if (current.IsEnabled == enabled) return;

        await service.SetEnabledAsync(actorId, new SetStorageDefaultEnabledCommand
        {
            DefaultId = current.Id, ExpectedXmin = current.Xmin, ExpectedRevision = current.Revision, IsEnabled = enabled,
        }, CancellationToken.None);
    }

    private async Task<Guid> SeedOwnRouteAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var profiles = scope.Resolve<Core.Services.Workflows.Artifacts.Profiles.IStorageProfileService>();
        var routes = scope.Resolve<IStorageRouteService>();

        var profile = await profiles.CreateAsync(world.TeamId, world.ActorId, new CreateStorageProfileCommand
        {
            StableName = "the-teams-own-choice",
            ProviderTypeKey = ProviderTypeKey,
            NonSecretConfig = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath = NewRoot() })).RootElement,
        }, CancellationToken.None);

        var active = await profiles.SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = profile.Id, ExpectedXmin = profile.Xmin, ExpectedCurrentRevision = profile.CurrentRevision,
            State = StorageProfileStateValue.Active,
        }, CancellationToken.None);

        var route = await routes.CreateAsync(world.TeamId, world.ActorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = AgentRunLogClass, StorageProfileId = active!.Id,
        }, CancellationToken.None);

        return route.Id;
    }

    /// <summary>A deployment operator — a user with no team. A template is instance scope, so its author is not a member of the team it will later be materialized for, and storage_default.created_by is a real foreign key.</summary>
    private static async Task<Guid> SeedOperatorAsync(ILifetimeScope scope)
    {
        var operatorId = Guid.NewGuid();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = operatorId, Email = $"operator-{operatorId:N}@test.local", Name = $"operator-{operatorId:N}" });
        await db.SaveChangesAsync();

        return operatorId;
    }

    private async Task<World> SeedTeamAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"materializer-{actorId:N}@test.local", Name = $"materializer-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"materializer-{teamId:N}", Name = "Materializer Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        return new World(teamId, actorId);
    }

    private async Task<IReadOnlyList<string>> FingerprintsAsync(params Guid[] profileIds)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfileRevision.AsNoTracking()
            .Where(revision => profileIds.Contains(revision.StorageProfileId))
            .Select(revision => revision.NamespaceFingerprint)
            .ToListAsync();
    }

    private async Task<Counts> CountAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return new Counts(
            await db.StorageProfile.AsNoTracking().CountAsync(row => row.TeamId == teamId),
            await db.StorageRoute.AsNoTracking().CountAsync(row => row.TeamId == teamId),
            await db.StorageDefaultMaterialization.AsNoTracking().CountAsync(row => row.TeamId == teamId));
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId);
    private sealed record Counts(int Profiles, int Routes, int Provenance);
}
