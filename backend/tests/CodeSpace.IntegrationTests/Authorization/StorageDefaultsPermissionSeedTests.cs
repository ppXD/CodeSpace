using System.Security.Claims;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Users;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Queries.Storage;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// The instance capability introduced by migration 0173, checked against a database the migrations have just built.
///
/// <para>A capability in this tier has TWO halves that can drift apart, and only one of them is obvious.
/// <c>GlobalPermissionAuthorizationBehavior</c> lets <c>Roles.Admin</c> through implicitly at ENFORCEMENT time, so the
/// <c>permission</c> row alone already gates the write correctly and a missing <c>role_permission</c> row breaks
/// nothing a test of the gate would notice. But the /me PROJECTION in <c>UserService</c> has no implicit-Admin branch:
/// it lists exactly the permissions reachable through <c>role_permission</c> or <c>user_permission</c>. Omit that
/// second row and the server accepts the write while /me never reports the capability — so a future admin UI hides the
/// control from the only account in the deployment that holds it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageDefaultsPermissionSeedTests
{
    private const string BootstrapAdminEmail = "admin@codespace.local";

    private readonly PostgresFixture _fixture;

    public StorageDefaultsPermissionSeedTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_capability_exists_as_a_permission_row()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var permission = await db.Permission.AsNoTracking().SingleOrDefaultAsync(value => value.Name == Permissions.StorageDefaultsManage);

        permission.ShouldNotBeNull($"migration 0173 must insert '{Permissions.StorageDefaultsManage}' into the permission table");
        permission.IsSystem.ShouldBeTrue();
    }

    /// <summary>
    /// THE TWO-ROW CHECK. Delete the <c>role_permission</c> INSERT from migration 0173 and this is the only test that
    /// notices — every enforcement test keeps passing, because Admin is let through implicitly.
    /// </summary>
    [Fact]
    public async Task The_bootstrap_admin_sees_the_capability_in_its_own_me_projection()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var admin = await db.User.AsNoTracking().SingleAsync(value => value.Email == BootstrapAdminEmail);

        var me = await scope.Resolve<IUserService>().BuildMeForAsync(admin, CancellationToken.None);

        me.Permissions.ShouldContain(
            Permissions.StorageDefaultsManage,
            customMessage: "the /me projection joins role_permission and user_permission and has NO implicit-Admin branch, so a missing role_permission row leaves the deployment's only holder unable to see the capability it has. Check the second INSERT in migration 0173.");
    }

    /// <summary>
    /// The bootstrap admin holds it BY ROLE, not by an individual grant — the deployment default must not be handed to
    /// every account, and <c>TeamInvitationService</c> (the one writer of <c>user_permission</c>) only ever grants
    /// <see cref="Permissions.GrantedToEveryAccount"/>.
    /// </summary>
    [Fact]
    public async Task The_capability_is_held_by_role_and_is_not_a_default_account_grant()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var permissionId = await db.Permission.AsNoTracking().Where(value => value.Name == Permissions.StorageDefaultsManage).Select(value => value.Id).SingleAsync();

        (await db.RolePermission.AsNoTracking().AnyAsync(value => value.PermissionId == permissionId && value.RoleId == SystemRoles.AdminId)).ShouldBeTrue();
        (await db.UserPermission.AsNoTracking().AnyAsync(value => value.PermissionId == permissionId)).ShouldBeFalse();
        Permissions.GrantedToEveryAccount.ShouldNotContain(Permissions.StorageDefaultsManage);
    }

    /// <summary>
    /// Fidelity: driven through the REAL <see cref="ApiUser"/>, so the grant is resolved by the production join over
    /// <c>role_user</c> and <c>role.status</c>.
    ///
    /// <para>This matters because <c>TestCurrentUser.HasRole</c> accepts any string it was handed, so a test using the
    /// fake cannot tell a live role from a disabled one. Here the same account is admitted and then refused by nothing
    /// more than <c>role.status</c> flipping — which is exactly the difference the fake erases.</para>
    /// </summary>
    [Fact]
    public async Task A_disabled_role_stops_granting_the_capability_through_the_real_principal()
    {
        var (userId, roleId) = await SeedRoleHolderAsync();

        await Should.NotThrowAsync(async () => await SendAsync(userId));

        await SetRoleStatusAsync(roleId, status: false);

        var denied = await Should.ThrowAsync<TenantAccessDeniedException>(async () => await SendAsync(userId));
        denied.Reason.ShouldContain(Permissions.StorageDefaultsManage);
    }

    private async Task SendAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(Accessor(userId)).As<IHttpContextAccessor>().SingleInstance());

        scope.Resolve<ICurrentUser>().ShouldBeOfType<ApiUser>("this test is worthless unless the production principal is the one being asked");
        await scope.Resolve<IMediator>().Send(new ListStorageDefaultsQuery());
    }

    /// <summary>A live role that carries the capability, held by a fresh account — never the Admin role, whose status every other test depends on.</summary>
    private async Task<(Guid UserId, Guid RoleId)> SeedRoleHolderAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var permissionId = await db.Permission.AsNoTracking().Where(value => value.Name == Permissions.StorageDefaultsManage).Select(value => value.Id).SingleAsync();
        var user = new User { Id = Guid.NewGuid(), Email = $"storage-admin-{suffix}@test.local", Name = "Storage Admin" };
        var role = new Role { Id = Guid.NewGuid(), Name = $"storage-defaults-{suffix}", IsSystem = false, Status = true };
        db.User.Add(user);
        db.Role.Add(role);
        db.RolePermission.Add(new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, PermissionId = permissionId });
        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = role.Id, UserId = user.Id });
        await db.SaveChangesAsync();
        return (user.Id, role.Id);
    }

    private async Task SetRoleStatusAsync(Guid roleId, bool status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var role = await db.Role.SingleAsync(value => value.Id == roleId);
        role.Status = status;
        await db.SaveChangesAsync();
    }

    private static IHttpContextAccessor Accessor(Guid userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, "storage-admin")], "IntegrationTest");
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }
}
