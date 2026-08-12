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
    /// Ownership is recorded twice — on <c>team.owner_user_id</c> and as an Owner membership row — and
    /// code that WRITES relies on the row being there. Transferring a team edits it to demote the
    /// outgoing owner; leaving and role changes load it. Reading code synthesises Owner from the team
    /// row when it is missing, which is why the seed workspace looked fine for months while handing it
    /// over silently dropped the person handing it over.
    ///
    /// <para>Every team created through the app writes both. This asserts the hand-written seed does
    /// too, and fails for any future migration that adds a team the same half-recorded way.</para>
    ///
    /// <para>Scoped to the two teams the migrations create, by slug. The fixture's database is shared
    /// across the whole collection and other tests seed teams by hand — asserting over every row would
    /// be reading their leftovers, and would fail or pass depending on what else ran.</para>
    /// </summary>
    [Theory]
    [InlineData("default")]              // 0006 — the workspace every fresh deployment lands in
    [InlineData("personal-00000000")]    // 0008 — the seed admin's personal team
    public async Task Every_seeded_team_records_its_owner_as_a_member_too(string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var team = await db.Team.AsNoTracking()
            .Where(t => t.Slug == slug && t.DeletedDate == null)
            .Select(t => new { t.Id, t.OwnerUserId })
            .SingleOrDefaultAsync().ConfigureAwait(false);

        team.ShouldNotBeNull($"the migrations seed a team with slug '{slug}'");

        var role = await db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == team.Id && m.UserId == team.OwnerUserId)
            .Select(m => (TeamRole?)m.Role)
            .SingleOrDefaultAsync().ConfigureAwait(false);

        role.ShouldBe(TeamRole.Owner, $"team '{slug}' names an owner that its membership rows do not");
    }
}
