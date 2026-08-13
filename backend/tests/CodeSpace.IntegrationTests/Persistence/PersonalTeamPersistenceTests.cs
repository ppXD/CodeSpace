using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// The one thing <c>team.personal_for_user_id</c> is still kept for.
///
/// <para>Ownership moved onto the membership row, and every other reason to keep a user id on the team
/// went with it. This did not: "one active Personal team per user" is a unique index over a column of
/// <c>team</c>, and Postgres cannot build one out of <c>team_membership</c>. Migration 0118 renamed the
/// column out from under the index, so this is the test that says the invariant came through the
/// rename rather than quietly becoming a column nothing enforces.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PersonalTeamPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public PersonalTeamPersistenceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_account_cannot_hold_two_active_personal_teams()
    {
        var userId = await SeedUserAsync().ConfigureAwait(false);

        await AddPersonalTeamAsync(userId, deleted: false).ConfigureAwait(false);

        await Should.ThrowAsync<DbUpdateException>(() => AddPersonalTeamAsync(userId, deleted: false)).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_closed_personal_team_does_not_block_opening_another()
    {
        // Partial on deleted_date for the same reason every other uniqueness rule here is: a team that
        // has been closed is history, and history must not stop somebody starting again.
        var userId = await SeedUserAsync().ConfigureAwait(false);

        await AddPersonalTeamAsync(userId, deleted: true).ConfigureAwait(false);

        await AddPersonalTeamAsync(userId, deleted: false).ConfigureAwait(false);
    }

    [Fact]
    public async Task Workspaces_are_not_limited_the_same_way()
    {
        // The column is NULL for a Workspace, and a partial unique index over NULLs constrains nothing
        // — which is what lets one account open as many workspaces as it likes.
        var userId = await SeedUserAsync().ConfigureAwait(false);

        await AddWorkspaceAsync(userId).ConfigureAwait(false);
        await AddWorkspaceAsync(userId).ConfigureAwait(false);
    }

    private async Task AddPersonalTeamAsync(Guid userId, bool deleted)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Slug = $"personal-{Guid.NewGuid():N}",
            Name = "Personal",
            Kind = TeamKind.Personal,
            PersonalForUserId = userId,
            DeletedDate = deleted ? DateTimeOffset.UtcNow : null,
        };

        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task AddWorkspaceAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var team = new Team { Id = Guid.NewGuid(), Slug = $"ws-{Guid.NewGuid():N}", Name = "Workspace", Kind = TeamKind.Workspace };

        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"personal-{Guid.NewGuid():N}@x", Name = "solo" };

        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }
}
