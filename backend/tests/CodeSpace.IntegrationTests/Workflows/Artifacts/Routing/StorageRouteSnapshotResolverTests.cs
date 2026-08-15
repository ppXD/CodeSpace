using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Routing;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageRouteSnapshotResolverTests
{
    private readonly PostgresFixture _fixture;

    public StorageRouteSnapshotResolverTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Current_at_write_resolves_each_new_write_to_the_then_current_immutable_profile_revision()
    {
        var world = await SeedWorldAsync(StorageProfileRevisionMode.CurrentAtWrite, "agent-run-log/v1");

        var first = (await ResolveAsync(world.TeamId, world.DataClassTypeKey)).ShouldBeOfType<StorageRouteSnapshotResolution.Ready>().Snapshot;
        await AppendProfileRevisionAsync(world, 2, 'b');
        var second = (await ResolveAsync(world.TeamId, world.DataClassTypeKey)).ShouldBeOfType<StorageRouteSnapshotResolution.Ready>().Snapshot;

        first.RouteId.ShouldBe(world.RouteId);
        first.RouteRevision.ShouldBe(1);
        first.StorageProfileRevision.ShouldBe(1);
        first.NamespaceFingerprint.ShouldBe(Fingerprint('a'));
        second.RouteId.ShouldBe(first.RouteId);
        second.RouteRevision.ShouldBe(first.RouteRevision);
        second.StorageProfileRevision.ShouldBe(2);
        second.NamespaceFingerprint.ShouldBe(Fingerprint('b'));
        first.StorageProfileRevision.ShouldBe(1, "an already-returned snapshot remains frozen after control-plane pointers advance");
    }

    [Fact]
    public async Task Pinned_route_stays_on_the_exact_old_profile_revision_after_the_profile_pointer_advances()
    {
        var world = await SeedWorldAsync(StorageProfileRevisionMode.Pinned, "workflow-run-model-call/v1");
        await AppendProfileRevisionAsync(world, 2, 'b');

        var snapshot = (await ResolveAsync(world.TeamId, world.DataClassTypeKey)).ShouldBeOfType<StorageRouteSnapshotResolution.Ready>().Snapshot;

        snapshot.StorageProfileRevision.ShouldBe(1);
        snapshot.NamespaceFingerprint.ShouldBe(Fingerprint('a'));
    }

    [Fact]
    public async Task One_team_scoped_statement_projects_only_route_and_non_secret_storage_coordinates()
    {
        var world = await SeedWorldAsync(StorageProfileRevisionMode.CurrentAtWrite, "artifact-cas/v1");
        var interceptor = new ReadCommandRecorder();
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new CodeSpaceDbContext(options);

        var result = await new StorageRouteSnapshotResolver(db).ResolveAsync(new StorageRouteSnapshotRequest(world.TeamId, world.DataClassTypeKey), CancellationToken.None);

        result.ShouldBeOfType<StorageRouteSnapshotResolution.Ready>();
        interceptor.ReadCommands.Count.ShouldBe(1);
        var sql = interceptor.ReadCommands.Single();
        sql.ShouldContain("storage_route_revision");
        sql.ShouldContain("storage_profile_revision");
        sql.ShouldNotContain("config_jsonb", Case.Insensitive);
        sql.ShouldNotContain("credential_ref", Case.Insensitive);
        sql.ShouldNotContain("encrypted_payload", Case.Insensitive);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_foreign_inactive_and_cancelled_routes_fail_closed_with_distinct_results()
    {
        var world = await SeedWorldAsync(StorageProfileRevisionMode.CurrentAtWrite, "agent-run-debug/v1");
        var foreign = await SeedTeamAsync();

        (await ResolveAsync(foreign.TeamId, world.DataClassTypeKey)).ShouldBeOfType<StorageRouteSnapshotResolution.Missing>();
        (await ResolveAsync(world.TeamId, "missing-data-class/v1")).ShouldBeOfType<StorageRouteSnapshotResolution.Missing>();

        await SetRouteStateAsync(world.RouteId, StorageRouteState.Disabled);
        (await ResolveAsync(world.TeamId, world.DataClassTypeKey)).ShouldBeOfType<StorageRouteSnapshotResolution.RouteNotActive>();
        await SetRouteStateAsync(world.RouteId, StorageRouteState.Active);
        await SetProfileStateAsync(world.ProfileId, StorageProfileState.Disabled);
        (await ResolveAsync(world.TeamId, world.DataClassTypeKey)).ShouldBeOfType<StorageRouteSnapshotResolution.ProfileNotActive>();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await ResolveAsync(world.TeamId, world.DataClassTypeKey, cancellation.Token);
        cancelled.ShouldBeOfType<StorageRouteSnapshotResolution.Cancelled>();
    }

    [Fact]
    public async Task Invalid_identity_and_unversioned_type_key_fail_before_issuing_a_database_command()
    {
        var interceptor = new ReadCommandRecorder();
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new CodeSpaceDbContext(options);
        var resolver = new StorageRouteSnapshotResolver(db);

        var invalidTeam = await resolver.ResolveAsync(new StorageRouteSnapshotRequest(Guid.Empty, "agent-run-log/v1"), CancellationToken.None);
        var invalidType = await resolver.ResolveAsync(new StorageRouteSnapshotRequest(Guid.NewGuid(), "agent-run-log"), CancellationToken.None);

        invalidTeam.ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.Request));
        invalidType.ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.DataClassTypeKey));
        interceptor.ReadCommands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Corrupt_future_profile_revision_mode_is_an_invalid_value_not_an_exception_or_fallback()
    {
        var world = await SeedWorldAsync(StorageProfileRevisionMode.CurrentAtWrite, "tool-attempt/v1");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE storage_route_revision DISABLE TRIGGER storage_route_revision_enforce_append_only");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE storage_route_revision DROP CONSTRAINT ck_storage_route_revision_profile_selection");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route_revision SET profile_revision_mode = 'FuturePolicy' WHERE storage_route_id = {world.RouteId}");

        var result = await new StorageRouteSnapshotResolver(db).ResolveAsync(new StorageRouteSnapshotRequest(world.TeamId, world.DataClassTypeKey), CancellationToken.None);

        result.ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevisionMode));
        await transaction.RollbackAsync();
    }

    private async Task<StorageRouteSnapshotResolution> ResolveAsync(Guid teamId, string dataClassTypeKey, CancellationToken cancellationToken = default)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IStorageRouteSnapshotResolver>().ResolveAsync(new StorageRouteSnapshotRequest(teamId, dataClassTypeKey), cancellationToken);
    }

    private async Task<World> SeedWorldAsync(StorageProfileRevisionMode mode, string dataClassTypeKey)
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"route-resolver-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(ProfileRevision(profile, actorId, 1, 'a'));
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = dataClassTypeKey, CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = 1, StorageProfileId = profile.Id,
            ProfileRevisionMode = mode, PinnedProfileRevision = mode == StorageProfileRevisionMode.Pinned ? 1 : null,
            CreatedDate = now, CreatedBy = actorId,
        });

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageProfile.Add(profile);
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();
        route.State = StorageRouteState.Active;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new World(teamId, actorId, route.Id, profile.Id, dataClassTypeKey);
    }

    private async Task AppendProfileRevisionAsync(World world, int revision, char fingerprint)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.SingleAsync(value => value.TeamId == world.TeamId && value.Id == world.ProfileId);
        profile.CurrentRevision = revision;
        profile.LastModifiedDate = DateTimeOffset.UtcNow;
        profile.LastModifiedBy = world.ActorId;
        db.StorageProfileRevision.Add(ProfileRevision(profile, world.ActorId, revision, fingerprint));
        await db.SaveChangesAsync();
    }

    private async Task SetRouteStateAsync(Guid routeId, StorageRouteState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.SingleAsync(value => value.Id == routeId);
        route.State = state;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SetProfileStateAsync(Guid profileId, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.SingleAsync(value => value.Id == profileId);
        profile.State = state;
        profile.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid ActorId)> SeedTeamAsync()
    {
        var teamId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"route-resolver-{actorId:N}@test.local", Name = $"route-resolver-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"route-resolver-{teamId:N}", Name = "Route Resolver Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return (teamId, actorId);
    }

    private static StorageProfileRevision ProfileRevision(StorageProfile profile, Guid actorId, int revision, char fingerprint) => new()
    {
        Id = Guid.NewGuid(), TeamId = profile.TeamId, StorageProfileId = profile.Id, Revision = revision,
        ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = $"{{\"rootPath\":\"/srv/route-resolver-v{revision}\"}}",
        CredentialRef = null, NamespaceFingerprint = Fingerprint(fingerprint), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static string Fingerprint(char value) => $"sha256:{new string(value, 64)}";

    private sealed record World(Guid TeamId, Guid ActorId, Guid RouteId, Guid ProfileId, string DataClassTypeKey);

    private sealed class ReadCommandRecorder : DbCommandInterceptor
    {
        public List<string> ReadCommands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ReadCommands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ReadCommands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
