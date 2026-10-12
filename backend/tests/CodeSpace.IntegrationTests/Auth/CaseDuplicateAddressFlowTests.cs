using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Commands.Invitations;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Auth;

/// <summary>
/// A deployment that already holds two live accounts at one address in different letter case — the state migration
/// 0245 leaves alone, creating no index and warning instead of refusing to run.
///
/// <para>Sign-in and invitations used to assume an address names one row (<c>SingleOrDefault</c>), so on such a
/// deployment both accounts answered "Sequence contains more than one element" to their own correct password, and so
/// did every invitation to the address. Each now resolves the exact spelling first and a case-insensitive match only
/// when it is unique, and the application itself refuses to create a further account at the address.</para>
///
/// <para>In <see cref="DeploymentDefaultsCollection"/> because whether <c>ux_app_user_email_lower</c> exists is
/// deployment-scope state: each test drops it for its own run and restores it afterwards, which no class sharing the
/// main database could tolerate.</para>
/// </summary>
[Collection(DeploymentDefaultsCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CaseDuplicateAddressFlowTests : IAsyncLifetime
{
    private const string UpperPassword = "upper-password-123";
    private const string LowerPassword = "lower-password-123";

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _seeded = new();

    public CaseDuplicateAddressFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync() => await ExecuteAsync("DROP INDEX IF EXISTS ux_app_user_email_lower").ConfigureAwait(false);

    /// <summary>Retires this test's accounts and puts the index back, so the database leaves as every other test expects it.</summary>
    public async Task DisposeAsync()
    {
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            await db.User.IgnoreQueryFilters().Where(u => _seeded.Contains(u.Id) && u.DeletedDate == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.DeletedDate, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        await ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email_lower ON app_user (lower(email)) WHERE deleted_date IS NULL").ConfigureAwait(false);
    }

    [Fact]
    public async Task Each_account_of_the_pair_signs_in_with_its_own_exact_address()
    {
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "dup.{0}@corp.test").ConfigureAwait(false);

        (await SignInAsync(pair.UpperEmail, UpperPassword).ConfigureAwait(false)).User.Id.ShouldBe(pair.Upper);
        (await SignInAsync(pair.LowerEmail, LowerPassword).ConfigureAwait(false)).User.Id.ShouldBe(pair.Lower);
    }

    [Fact]
    public async Task A_spelling_that_matches_neither_exactly_signs_in_nobody()
    {
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "dup.{0}@corp.test").ConfigureAwait(false);
        var third = pair.LowerEmail.ToUpperInvariant();

        var asUpper = async () => await SignInAsync(third, UpperPassword).ConfigureAwait(false);
        var asLower = async () => await SignInAsync(third, LowerPassword).ConfigureAwait(false);

        await asUpper.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
        await asLower.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task An_invitation_to_an_address_neither_spelling_matches_exactly_is_issued_and_either_account_accepts_it()
    {
        // Invitations store the address lower-cased, so with neither account spelled that way the address is ambiguous:
        // the invite must still go out, and whichever of the two signs in may take it.
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "DUP.{0}@corp.TEST").ConfigureAwait(false);
        var team = await SeedWorkspaceAsync().ConfigureAwait(false);

        var invite = await InviteAsync(team, pair.LowerEmail).ConfigureAwait(false);
        var accepted = await AcceptAsync(TokenOf(invite), pair.Lower, name: null, password: null).ConfigureAwait(false);

        accepted.User.Id.ShouldBe(pair.Lower);
        (await IsMemberAsync(team.TeamId, pair.Lower).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_anonymous_accept_at_a_duplicated_address_creates_no_third_account()
    {
        // The application's own refusal, with no index to fall back on: an address held in ANY letter case is held.
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "DUP.{0}@corp.TEST").ConfigureAwait(false);
        var team = await SeedWorkspaceAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, pair.LowerEmail).ConfigureAwait(false);

        var act = async () => await AcceptAsync(TokenOf(invite), userId: null, $"Third {Guid.NewGuid():N}", "correct-horse-battery").ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationRequiresSignInException>().ConfigureAwait(false);
        (await CountLiveAtAsync(pair.LowerEmail).ConfigureAwait(false)).ShouldBe(2, "no third account may be created at an address two accounts already hold");
    }

    [Fact]
    public async Task Inviting_the_exact_spelling_of_a_member_is_refused_as_already_a_member()
    {
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "dup.{0}@corp.test").ConfigureAwait(false);
        var team = await SeedWorkspaceAsync(alsoMember: pair.Lower).ConfigureAwait(false);

        var act = async () => await InviteAsync(team, pair.LowerEmail).ConfigureAwait(false);

        await act.ShouldThrowAsync<AlreadyTeamMemberException>().ConfigureAwait(false);
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record Pair(Guid Upper, string UpperEmail, Guid Lower, string LowerEmail);

    private sealed record Workspace(Guid TeamId, Guid Owner);

    private async Task<Pair> SeedPairAsync(string upperShape, string lowerShape)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var upperEmail = string.Format(upperShape, suffix);
        var lowerEmail = string.Format(lowerShape, suffix);

        var upper = await SeedAccountAsync(upperEmail, UpperPassword).ConfigureAwait(false);
        var lower = await SeedAccountAsync(lowerEmail, LowerPassword).ConfigureAwait(false);

        return new Pair(upper, upperEmail, lower, lowerEmail);
    }

    private async Task<Guid> SeedAccountAsync(string email, string password)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = email, Name = $"Duplicate {Guid.NewGuid():N}", PasswordHash = scope.Resolve<IPasswordHasher>().Hash(password) };

        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _seeded.Add(user.Id);

        return user.Id;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid? alsoMember = null)
    {
        var owner = await SeedAccountAsync($"owner-{Guid.NewGuid():N}@corp.test", "owner-password-123").ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var team = new Team { Id = Guid.NewGuid(), Slug = $"dup-{Guid.NewGuid():N}"[..20], Name = "Duplicates", Kind = TeamKind.Workspace };

        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner, Role = TeamRole.Owner });

        if (alsoMember is { } member) db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member, Role = TeamRole.Member });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Workspace(team.Id, owner);
    }

    private async Task<SignInResponse> SignInAsync(string identifier, string password)
    {
        using var scope = _fixture.BeginScopeAs(userId: null, teamId: null);

        return await scope.Resolve<IMediator>().Send(new SignInCommand { Name = identifier, Password = password }).ConfigureAwait(false);
    }

    private async Task<Messages.Dtos.Invitations.CreateInvitationResult> InviteAsync(Workspace team, string email)
    {
        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);

        return await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = email, Role = TeamRole.Member }).ConfigureAwait(false);
    }

    private async Task<SignInResponse> AcceptAsync(string token, Guid? userId, string? name, string? password)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId: null);

        return await scope.Resolve<IMediator>().Send(new AcceptInvitationCommand { Token = token, Name = name, Password = password }).ConfigureAwait(false);
    }

    private async Task<bool> IsMemberAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().TeamMembership.AsNoTracking().AnyAsync(m => m.TeamId == teamId && m.UserId == userId).ConfigureAwait(false);
    }

    private async Task<int> CountLiveAtAsync(string address)
    {
        using var scope = _fixture.BeginScope();
        var folded = address.ToLowerInvariant();

        return await scope.Resolve<CodeSpaceDbContext>().User.AsNoTracking().CountAsync(u => u.DeletedDate == null && u.Email.ToLower() == folded).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
    }

    private static string TokenOf(Messages.Dtos.Invitations.CreateInvitationResult invite) => invite.InviteUrl[(invite.InviteUrl.LastIndexOf('/') + 1)..];
}
