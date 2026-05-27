using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Pins the RBAC seed data created by migration 0004. The seeded system user + Admin role
/// are referenced from <see cref="SystemUsers"/> / <see cref="SystemRoles"/> constants;
/// changing those constants in code without matching the migration would orphan the rows
/// and break every audit-trail join. These tests fail the build if either drifts.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RbacSeedTests
{
    private readonly PostgresFixture _fixture;

    public RbacSeedTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void SystemUsers_SeederId_constant_pinned() => SystemUsers.SeederId.ShouldBe(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    [Fact]
    public void SystemRoles_AdminId_constant_pinned() => SystemRoles.AdminId.ShouldBe(Guid.Parse("00000000-0000-0000-0000-000000000010"));

    [Fact]
    public void Roles_Admin_constant_pinned() => Roles.Admin.ShouldBe("Admin");

    [Fact]
    public async Task Seeded_system_user_exists_in_db()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var user = await db.User.AsNoTracking().SingleOrDefaultAsync(u => u.Id == SystemUsers.SeederId).ConfigureAwait(false);

        user.ShouldNotBeNull();
        user.Email.ShouldBe(SystemUsers.SeederEmail);
        user.Name.ShouldBe(SystemUsers.SeederName);
    }

    [Fact]
    public async Task Seeded_Admin_role_exists_and_is_system()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var role = await db.Role.AsNoTracking().SingleOrDefaultAsync(r => r.Id == SystemRoles.AdminId).ConfigureAwait(false);

        role.ShouldNotBeNull();
        role.Name.ShouldBe(Roles.Admin);
        role.IsSystem.ShouldBeTrue("Admin role must be flagged IsSystem so UI cannot delete it");
        role.Status.ShouldBeTrue();
    }

    [Fact]
    public async Task Seeded_system_user_is_assigned_to_Admin_role()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var hasAssignment = await db.RoleUser.AsNoTracking()
            .AnyAsync(ru => ru.UserId == SystemUsers.SeederId && ru.RoleId == SystemRoles.AdminId)
            .ConfigureAwait(false);

        hasAssignment.ShouldBeTrue();
    }
}
