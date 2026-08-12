using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// The grants every account is supposed to hold, checked against a database the migrations have just
/// built.
///
/// <para>A default grant has two halves that can drift apart: the migration that gives it to the
/// accounts that already existed, and the code that gives it to the ones made afterwards. Adding a
/// permission to <c>Permissions.GrantedToEveryAccount</c> and forgetting the backfill leaves every
/// existing account quietly short of it, which nothing else would notice.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DefaultAccountPermissionsTests
{
    private readonly PostgresFixture _fixture;

    public DefaultAccountPermissionsTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// Pinned so that widening the set is a deliberate edit here, next to the note that says a new
    /// entry needs its own backfill migration.
    /// </summary>
    [Fact]
    public void The_set_of_default_grants_is_pinned()
    {
        Permissions.GrantedToEveryAccount.ShouldBe([Permissions.TeamsCreate]);
    }

    [Fact]
    public async Task Every_default_grant_names_a_permission_that_exists()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var known = await db.Permission.AsNoTracking().Select(p => p.Name).ToListAsync().ConfigureAwait(false);

        foreach (var name in Permissions.GrantedToEveryAccount)
            known.ShouldContain(name, $"'{name}' is granted to every account but no migration inserts it into the permission table");
    }

    /// <summary>
    /// The seed admin is the account every deployment starts with. It holds the grants by role as
    /// well, so this is not what lets it through — but if the backfill missed it, it missed everyone.
    /// </summary>
    [Fact]
    public async Task The_seeded_account_holds_them()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var held = await HeldByAsync(db, "admin@codespace.local").ConfigureAwait(false);

        foreach (var name in Permissions.GrantedToEveryAccount) held.ShouldContain(name);
    }

    [Fact]
    public async Task The_bot_holds_none_of_them()
    {
        // It is a member of teams but not a person. Nothing should hand it the ability to make more.
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var botIds = await db.User.AsNoTracking().IgnoreQueryFilters().Where(u => u.IsBot).Select(u => u.Id).ToListAsync().ConfigureAwait(false);

        var granted = await db.UserPermission.AsNoTracking().Where(up => botIds.Contains(up.UserId)).CountAsync().ConfigureAwait(false);

        granted.ShouldBe(0);
    }

    private static async Task<IReadOnlyList<string>> HeldByAsync(CodeSpaceDbContext db, string email)
    {
        var userId = await db.User.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).SingleAsync().ConfigureAwait(false);

        return await db.UserPermission.AsNoTracking()
            .Where(up => up.UserId == userId)
            .Join(db.Permission.AsNoTracking(), up => up.PermissionId, p => p.Id, (_, p) => p.Name)
            .ToListAsync().ConfigureAwait(false);
    }
}
