using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that team storage routing is append-only, tenant-bound and explicit about current-at-write
/// versus exact pinned profile revisions. The schema has no runtime consumer and cannot affect completion semantics.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageRoutePersistenceTests
{
    private readonly PostgresFixture _fixture;

    public StorageRoutePersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Current_at_write_and_pinned_routes_round_trip_with_open_versioned_data_classes()
    {
        var seeded = await SeedProfileAsync();
        var current = Route(seeded.TeamId, "agent-run-log/v1", seeded.ActorId);
        current.Revisions.Add(Revision(current, seeded.Profile.Id, 1, seeded.ActorId));
        var pinned = Route(seeded.TeamId, "workflow-run-model-call/v1", seeded.ActorId);
        pinned.Revisions.Add(Revision(pinned, seeded.Profile.Id, 1, seeded.ActorId, 1));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageRoute.AddRange(current, pinned);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var routes = await db.StorageRoute.AsNoTracking().Include(route => route.Revisions)
                .Where(route => route.TeamId == seeded.TeamId).OrderBy(route => route.DataClassTypeKey).ToListAsync();
            routes.Count.ShouldBe(2);
            routes[0].DataClassTypeKey.ShouldBe("agent-run-log/v1");
            routes[0].Revisions.ShouldHaveSingleItem().ProfileRevisionMode.ShouldBe(StorageProfileRevisionMode.CurrentAtWrite);
            routes[0].Revisions.ShouldHaveSingleItem().PinnedProfileRevision.ShouldBeNull();
            routes[1].DataClassTypeKey.ShouldBe("workflow-run-model-call/v1");
            routes[1].Revisions.ShouldHaveSingleItem().ProfileRevisionMode.ShouldBe(StorageProfileRevisionMode.Pinned);
            routes[1].Revisions.ShouldHaveSingleItem().PinnedProfileRevision.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Append_requires_an_atomic_one_step_pointer_advance_and_history_is_immutable()
    {
        var seeded = await SeedRouteAsync("artifact-cas/v1");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var route = await db.StorageRoute.SingleAsync(candidate => candidate.Id == seeded.Route.Id);
            route.CurrentRevision = 2;
            db.StorageRouteRevision.Add(Revision(route, seeded.Profile.Id, 2, seeded.ActorId, 1));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revisions = await db.StorageRouteRevision.AsNoTracking().Where(revision => revision.StorageRouteId == seeded.Route.Id)
                .OrderBy(revision => revision.Revision).Select(revision => revision.Revision).ToListAsync();
            revisions.ShouldBe(new[] { 1, 2 });
        }

        await AssertDatabaseRejectedAsync(async db =>
        {
            var route = await db.StorageRoute.SingleAsync(candidate => candidate.Id == seeded.Route.Id);
            db.StorageRouteRevision.Add(Revision(route, seeded.Profile.Id, 3, seeded.ActorId, 1));
        }, "atomically advance");

        await AssertDatabaseRejectedAsync(async db =>
        {
            var route = await db.StorageRoute.SingleAsync(candidate => candidate.Id == seeded.Route.Id);
            route.CurrentRevision = 4;
        }, "advances exactly once");

        await AssertDatabaseRejectedAsync(async db =>
        {
            var revision = await db.StorageRouteRevision.SingleAsync(candidate => candidate.StorageRouteId == seeded.Route.Id && candidate.Revision == 1);
            revision.PinnedProfileRevision = null;
            revision.ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite;
        }, "immutable");
    }

    [Fact]
    public async Task Tenant_profile_revision_selection_and_type_key_constraints_fail_closed()
    {
        var seeded = await SeedRouteAsync("agent-run-log/v1");
        var other = await SeedProfileAsync();

        await AssertSaveRejectedAsync(RouteWithRevision(seeded.TeamId, "not-versioned", seeded.ActorId, seeded.Profile.Id));
        await AssertSaveRejectedAsync(RouteWithRevision(seeded.TeamId, "agent-run-log/v1", seeded.ActorId, seeded.Profile.Id));
        await AssertSaveRejectedAsync(RouteWithRevision(seeded.TeamId, "tool-attempt/v1", seeded.ActorId, seeded.Profile.Id, pinnedRevision: 99));
        await AssertSaveRejectedAsync(RouteWithRevision(seeded.TeamId, "workflow-run-model-call/v1", seeded.ActorId, other.Profile.Id));

        var malformedCurrent = RouteWithRevision(seeded.TeamId, "workflow-run-event/v1", seeded.ActorId, seeded.Profile.Id);
        malformedCurrent.Revisions.Single().PinnedProfileRevision = 1;
        await AssertSaveRejectedAsync(malformedCurrent);

        var malformedPinned = RouteWithRevision(seeded.TeamId, "agent-run-debug/v1", seeded.ActorId, seeded.Profile.Id, pinnedRevision: 1);
        malformedPinned.Revisions.Single().PinnedProfileRevision = null;
        await AssertSaveRejectedAsync(malformedPinned);
    }

    [Fact]
    public async Task Route_identity_must_start_at_revision_one_in_draft_state()
    {
        var seeded = await SeedProfileAsync();
        var skipped = Route(seeded.TeamId, "artifact-cas/v1", seeded.ActorId);
        skipped.CurrentRevision = 5;
        skipped.Revisions.Add(Revision(skipped, seeded.Profile.Id, 5, seeded.ActorId, 1));
        await AssertSaveRejectedAsync(skipped);

        var prematurelyActive = RouteWithRevision(seeded.TeamId, "agent-run-log/v1", seeded.ActorId, seeded.Profile.Id, 1);
        prematurelyActive.State = StorageRouteState.Active;
        await AssertSaveRejectedAsync(prematurelyActive);
    }

    [Fact]
    public async Task Retired_route_is_terminal_and_rejects_new_revisions()
    {
        var seeded = await SeedRouteAsync("artifact-cas/v1");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var route = await db.StorageRoute.SingleAsync(candidate => candidate.Id == seeded.Route.Id);
            route.State = StorageRouteState.Retired;
            await db.SaveChangesAsync();
        }

        await AssertDatabaseRejectedAsync(async db =>
        {
            var route = await db.StorageRoute.SingleAsync(candidate => candidate.Id == seeded.Route.Id);
            route.State = StorageRouteState.Active;
        }, "terminal");

        await AssertDatabaseRejectedAsync(async db =>
        {
            var route = await db.StorageRoute.SingleAsync(candidate => candidate.Id == seeded.Route.Id);
            route.CurrentRevision = 2;
            db.StorageRouteRevision.Add(Revision(route, seeded.Profile.Id, 2, seeded.ActorId, 1));
        }, "rejects new revisions");

        var appendThenRetire = await SeedRouteAsync("workflow-run-model-call/v1");
        using var orderedScope = _fixture.BeginScope();
        var orderedDb = orderedScope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await orderedDb.Database.BeginTransactionAsync();
        var revisionId = Guid.NewGuid();
        var createdDate = DateTimeOffset.UtcNow;
        await orderedDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO storage_route_revision
                (id, team_id, storage_route_id, revision, storage_profile_id, profile_revision_mode,
                 pinned_profile_revision, created_date, created_by)
            VALUES ({revisionId}, {appendThenRetire.TeamId}, {appendThenRetire.Route.Id}, 2,
                    {appendThenRetire.Profile.Id}, 'Pinned', 1, {createdDate}, {appendThenRetire.ActorId})
            """);
        await orderedDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE storage_route SET current_revision = 2, state = 'Retired', last_modified_date = {createdDate},
                last_modified_by = {appendThenRetire.ActorId}
            WHERE team_id = {appendThenRetire.TeamId} AND id = {appendThenRetire.Route.Id}
            """);
        var orderedError = await transaction.CommitAsync().ShouldThrowAsync<PostgresException>();
        orderedError.SqlState.ShouldBe("P7501");
        orderedError.Message.ShouldContain("final state is Retired");
    }

    [Fact]
    public async Task Xmin_rejects_stale_route_policy_changes()
    {
        var seeded = await SeedRouteAsync("artifact-cas/v1");
        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        var firstDb = firstScope.Resolve<CodeSpaceDbContext>();
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();
        var first = await firstDb.StorageRoute.SingleAsync(route => route.Id == seeded.Route.Id);
        var second = await secondDb.StorageRoute.SingleAsync(route => route.Id == seeded.Route.Id);

        first.State = StorageRouteState.Active;
        await firstDb.SaveChangesAsync();
        second.State = StorageRouteState.Disabled;
        await secondDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Stale_append_writers_cannot_both_publish_the_same_next_revision()
    {
        var seeded = await SeedRouteAsync("artifact-cas/v1");
        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        var firstDb = firstScope.Resolve<CodeSpaceDbContext>();
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();
        var first = await firstDb.StorageRoute.SingleAsync(route => route.Id == seeded.Route.Id);
        var second = await secondDb.StorageRoute.SingleAsync(route => route.Id == seeded.Route.Id);

        first.CurrentRevision = 2;
        firstDb.StorageRouteRevision.Add(Revision(first, seeded.Profile.Id, 2, seeded.ActorId, 1));
        second.CurrentRevision = 2;
        secondDb.StorageRouteRevision.Add(Revision(second, seeded.Profile.Id, 2, seeded.ActorId, 1));

        await firstDb.SaveChangesAsync();
        await secondDb.SaveChangesAsync().ShouldThrowAsync<Exception>();

        using var readScope = _fixture.BeginScope();
        var revisions = await readScope.Resolve<CodeSpaceDbContext>().StorageRouteRevision.AsNoTracking()
            .CountAsync(revision => revision.StorageRouteId == seeded.Route.Id && revision.Revision == 2);
        revisions.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_profile_revision_mode_is_rejected_by_the_database()
    {
        var seeded = await SeedRouteAsync("artifact-cas/v1");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revisionId = Guid.NewGuid();
        var createdDate = DateTimeOffset.UtcNow;
        var error = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO storage_route_revision
                (id, team_id, storage_route_id, revision, storage_profile_id, profile_revision_mode,
                 pinned_profile_revision, created_date, created_by)
            VALUES ({revisionId}, {seeded.TeamId}, {seeded.Route.Id}, 2, {seeded.Profile.Id},
                    'FuturePolicy', NULL, {createdDate}, {seeded.ActorId})
            """).ShouldThrowAsync<PostgresException>();
        error.Message.ShouldContain("ck_storage_route_revision_profile_selection");
    }

    private async Task AssertDatabaseRejectedAsync(Func<CodeSpaceDbContext, Task> arrange, string expected)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await arrange(db);
        var error = await db.SaveChangesAsync().ShouldThrowAsync<Exception>();
        (error.InnerException?.Message ?? error.Message).ShouldContain(expected, Case.Insensitive);
    }

    private async Task AssertSaveRejectedAsync(StorageRoute route)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
    }

    private async Task<(StorageRoute Route, StorageProfile Profile, Guid TeamId, Guid ActorId)> SeedRouteAsync(string dataClassTypeKey)
    {
        var seeded = await SeedProfileAsync();
        var route = Route(seeded.TeamId, dataClassTypeKey, seeded.ActorId);
        route.Revisions.Add(Revision(route, seeded.Profile.Id, 1, seeded.ActorId, 1));
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();
        return (route, seeded.Profile, seeded.TeamId, seeded.ActorId);
    }

    private async Task<(StorageProfile Profile, Guid TeamId, Guid ActorId)> SeedProfileAsync()
    {
        var teamId = await SeedTeamAsync();
        var actorId = Guid.NewGuid();
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"route-store-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
            LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = "{\"rootPath\":\"/srv/codespace/artifacts\"}",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        });
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return (profile, teamId, actorId);
    }

    private static StorageRoute Route(Guid teamId, string dataClassTypeKey, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = dataClassTypeKey, CurrentRevision = 1,
        State = StorageRouteState.Draft, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId,
    };

    private static StorageRoute RouteWithRevision(Guid teamId, string dataClassTypeKey, Guid actorId, Guid profileId, int? pinnedRevision = null)
    {
        var route = Route(teamId, dataClassTypeKey, actorId);
        route.Revisions.Add(Revision(route, profileId, 1, actorId, pinnedRevision));
        return route;
    }

    private static StorageRouteRevision Revision(StorageRoute route, Guid profileId, int revision, Guid actorId, int? pinnedRevision = null) => new()
    {
        Id = Guid.NewGuid(), TeamId = route.TeamId, StorageRouteId = route.Id, Revision = revision,
        StorageProfileId = profileId,
        ProfileRevisionMode = pinnedRevision.HasValue ? StorageProfileRevisionMode.Pinned : StorageProfileRevisionMode.CurrentAtWrite,
        PinnedProfileRevision = pinnedRevision, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"storage-route-{userId:N}@test.local", Name = $"storage-route-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"storage-route-{teamId:N}", Name = "Storage Route Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }
}
