using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Queries.Users;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Auth;

/// <summary>
/// Pins the bootstrap-rotation behaviour: the seeded admin can sign in, but every
/// non-rotation request is gated until they rotate. After rotation the flag clears and
/// normal API access resumes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ChangePasswordFlowTests
{
    private readonly PostgresFixture _fixture;
    private const string SeedAdminEmail = "admin@codespace.local";
    private const string SeedAdminPassword = "changeme123";
    private static readonly Guid SeedAdminId = new("00000000-0000-0000-0000-000000000100");

    public ChangePasswordFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task SignIn_reports_PasswordMustChange_for_seed_admin()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = SeedAdminEmail, Password = SeedAdminPassword }).ConfigureAwait(false);

        result.User.PasswordMustChange.ShouldBeTrue();
    }

    [Fact]
    public async Task Rotation_gate_blocks_every_non_change_password_request()
    {
        // Any request from a flagged user other than ChangePasswordCommand must fail.
        using var scope = _fixture.BeginScopeAs(SeedAdminId, teamId: null);
        // Force the rotation flag on TestCurrentUser since the in-memory ICurrentUser is
        // the test double, not ApiUser — explicitly opt the test into rotation gating.
        using var rotationScope = scope.BeginLifetimeScope(b =>
            b.RegisterInstance(new TestCurrentUser(SeedAdminId, "admin", Roles.Admin) { PasswordMustChange = true })
                .As<CodeSpace.Core.Services.Identity.ICurrentUser>()
                .SingleInstance());

        var mediator = rotationScope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new MeQuery()).ConfigureAwait(false);

        await act.ShouldThrowAsync<PasswordRotationRequiredException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task ChangePassword_with_correct_current_clears_flag_and_lets_old_password_fail()
    {
        var newPassword = $"NewPassword-{Guid.NewGuid():N}";

        // Sign in (still pre-rotation) to confirm the start state.
        using (var scope = _fixture.BeginScope())
        {
            var initial = await scope.Resolve<IMediator>().Send(new SignInCommand { Name = SeedAdminEmail, Password = SeedAdminPassword }).ConfigureAwait(false);
            initial.User.PasswordMustChange.ShouldBeTrue();
        }

        // Rotate (must run as the admin — ICurrentUser is the test double).
        using (var rotateScope = _fixture.BeginScopeAs(SeedAdminId, teamId: null, Roles.Admin))
        {
            var result = await rotateScope.Resolve<IMediator>().Send(new ChangePasswordCommand
            {
                CurrentPassword = SeedAdminPassword,
                NewPassword = newPassword
            }).ConfigureAwait(false);

            result.User.PasswordMustChange.ShouldBeFalse();
        }

        // Old password should now fail.
        using (var oldScope = _fixture.BeginScope())
        {
            var oldAct = async () => await oldScope.Resolve<IMediator>().Send(new SignInCommand { Name = SeedAdminEmail, Password = SeedAdminPassword }).ConfigureAwait(false);
            await oldAct.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
        }

        // New password should work and the flag should be cleared.
        using (var newScope = _fixture.BeginScope())
        {
            var resign = await newScope.Resolve<IMediator>().Send(new SignInCommand { Name = SeedAdminEmail, Password = newPassword }).ConfigureAwait(false);
            resign.User.PasswordMustChange.ShouldBeFalse();
        }

        // Reset the DB state so sibling tests still see the seeded credentials.
        await ResetSeedAdminAsync(SeedAdminPassword).ConfigureAwait(false);
    }

    [Fact]
    public async Task ChangePassword_with_wrong_current_throws_InvalidCredentials()
    {
        using var scope = _fixture.BeginScopeAs(SeedAdminId, teamId: null, Roles.Admin);
        var act = async () => await scope.Resolve<IMediator>().Send(new ChangePasswordCommand
        {
            CurrentPassword = "definitely-not-the-password",
            NewPassword = "SomeLongEnoughNewOne1"
        }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task ChangePassword_rejects_short_or_unchanged_new_password()
    {
        using var scope = _fixture.BeginScopeAs(SeedAdminId, teamId: null, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var shortAct = async () => await mediator.Send(new ChangePasswordCommand
        {
            CurrentPassword = SeedAdminPassword,
            NewPassword = "short"
        }).ConfigureAwait(false);
        await shortAct.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);

        var sameAct = async () => await mediator.Send(new ChangePasswordCommand
        {
            CurrentPassword = SeedAdminPassword,
            NewPassword = SeedAdminPassword
        }).ConfigureAwait(false);
        await sameAct.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }

    // Reset the seed admin to the documented bootstrap password so the rest of the suite
    // keeps the same starting state regardless of execution order.
    private async Task ResetSeedAdminAsync(string plainPassword)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var hasher = scope.Resolve<CodeSpace.Core.Services.Auth.IPasswordHasher>();

        var admin = await db.User.FirstAsync(u => u.Id == SeedAdminId).ConfigureAwait(false);
        admin.PasswordHash = hasher.Hash(plainPassword);
        admin.PasswordMustChange = true;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
