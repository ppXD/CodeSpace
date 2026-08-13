using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Teams;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Invitations;

/// <summary>
/// The slug a brand-new account's personal workspace is created with, against the index that decides
/// whether the account can exist at all — <c>idx_team_slug</c> is unique on the slug alone, across the
/// whole instance, where every other slugged table scopes uniqueness to its own team.
///
/// <para>Driven through <see cref="TeamSlugAllocator"/> rather than through accepting an invitation,
/// because the accepting account's id is minted inside that path: the collision cannot be staged on the
/// wire without knowing the id in advance. This is the same production class the accept path calls,
/// against the same database and the same index, so the claim under test — that an occupied slug does
/// not become a failed signup — is the real one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PersonalWorkspaceSlugTests
{
    private readonly PostgresFixture _fixture;

    public PersonalWorkspaceSlugTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_account_still_gets_a_personal_workspace_when_something_already_holds_the_slug()
    {
        // Eight hex characters were trusted to be unique forever against a global index. They are not:
        // since every account may open a workspace, anyone can take personal-{8 hex} deliberately, and
        // whoever's id starts with those characters then hits a duplicate key while accepting their
        // invitation — a hard failure on the last statement of signup, not something that retries.
        var user = await SeedUserAsync().ConfigureAwait(false);
        var wanted = $"personal-{user.Id.ToString("N")[..8]}";

        await SeedTeamAsync(wanted, user.Id, TeamKind.Workspace).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var allocated = await scope.Resolve<TeamSlugAllocator>().ForPersonalAsync(user.Id, CancellationToken.None).ConfigureAwait(false);

        allocated.ShouldNotBe(wanted, "the slug is already spoken for, and signup cannot be the thing that discovers it");
        allocated.ShouldStartWith("personal-", customMessage: "still a personal workspace — migration 0008's shape is what the rest of the product reads");

        // The claim is about the index rather than the string, so the row has to actually go in. Without
        // the allocation above this insert is the reported ERROR: duplicate key value violates unique
        // constraint "idx_team_slug".
        await SeedTeamAsync(allocated, user.Id, TeamKind.Personal).ConfigureAwait(false);
    }

    [Fact]
    public async Task An_uncontested_personal_slug_is_still_the_one_migration_0008_would_have_written()
    {
        // Deduping must not drift the ordinary case: a personal team made today and one backfilled by
        // 0008 have to stay indistinguishable.
        var user = await SeedUserAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var allocated = await scope.Resolve<TeamSlugAllocator>().ForPersonalAsync(user.Id, CancellationToken.None).ConfigureAwait(false);

        allocated.ShouldBe($"personal-{user.Id.ToString("N")[..8]}");
    }

    private async Task<User> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"squatted-{Guid.NewGuid():N}@x", Name = "Newcomer" };

        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user;
    }

    /// <summary>Inserts and saves, so a slug the index refuses fails here with the constraint by name.</summary>
    private async Task SeedTeamAsync(string slug, Guid ownerId, TeamKind kind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var team = new Team { Id = Guid.NewGuid(), Slug = slug, Name = kind == TeamKind.Personal ? "Personal" : "Squatter", Kind = kind, PersonalForUserId = kind == TeamKind.Personal ? ownerId : null };

        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = ownerId, Role = TeamRole.Owner });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
