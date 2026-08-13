using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// What the migrations leave behind, asserted against a database they have just built.
///
/// <para>The fixture runs the whole DbUp chain on an empty database, so these read the state a brand-new
/// deployment actually starts in — the one state no integration test seeds by hand and every operator
/// gets.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SeedDataInvariantTests
{
    private readonly PostgresFixture _fixture;

    public SeedDataInvariantTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// An Owner membership row is the ONLY record of ownership, so a seeded team without one has no
    /// owner: nobody who can transfer it, invite an owner into it, or delete it. 0006 wrote the seed
    /// workspace by hand with the old <c>owner_user_id</c> column and no row, and 0116 repaired it;
    /// this fails for any future migration that seeds a team the same way.
    ///
    /// <para>Scoped to the two teams the migrations create, by slug. The fixture's database is shared
    /// across the whole collection and other tests seed teams by hand — asserting over every row would
    /// be reading their leftovers, and would fail or pass depending on what else ran.</para>
    /// </summary>
    [Theory]
    [InlineData("default")]              // 0006 — the workspace every fresh deployment lands in
    [InlineData("personal-00000000")]    // 0008 — the seed admin's personal team
    public async Task Every_seeded_team_has_an_owner(string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var teamId = await db.Team.AsNoTracking()
            .Where(t => t.Slug == slug && t.DeletedDate == null)
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync().ConfigureAwait(false);

        teamId.ShouldNotBeNull($"the migrations seed a team with slug '{slug}'");

        var owners = await db.TeamMembership.AsNoTracking()
            .CountAsync(m => m.TeamId == teamId.Value && m.Role == TeamRole.Owner).ConfigureAwait(false);

        owners.ShouldBe(1, $"team '{slug}' has no Owner membership row, so it has no owner at all");
    }

    /// <summary>
    /// What is left of the old owner column after 0118: it says which account a Personal team IS, and
    /// nothing at all about a Workspace. It is kept only because the "one active Personal team per
    /// user" unique index cannot be built out of <c>team_membership</c>, and a Workspace carrying a
    /// value here would be a second answer to ownership growing back.
    /// </summary>
    [Theory]
    [InlineData("default", false)]
    [InlineData("personal-00000000", true)]
    public async Task Only_a_personal_team_is_marked_as_someones_own(string slug, bool expectMarked)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var team = await db.Team.AsNoTracking()
            .Where(t => t.Slug == slug && t.DeletedDate == null)
            .Select(t => new { t.Id, t.PersonalForUserId })
            .SingleOrDefaultAsync().ConfigureAwait(false);

        team.ShouldNotBeNull($"the migrations seed a team with slug '{slug}'");

        if (!expectMarked)
        {
            team.PersonalForUserId.ShouldBeNull("a Workspace is nobody's personal space");
            return;
        }

        var ownerId = await db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == team.Id && m.Role == TeamRole.Owner)
            .Select(m => m.UserId)
            .SingleAsync().ConfigureAwait(false);

        team.PersonalForUserId.ShouldBe(ownerId, "a Personal team belongs to the account it was opened for");
    }
}
