using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Whose name a credential may be connected under. Both connect paths take the owner from the request — a token's
/// <c>ownerUserId</c>, an OAuth connect's <c>intendedOwnerUserId</c> — and the owner is who the team then acts AS: the
/// token is listed as theirs, an OAuth connect links their act-as identity, and a disconnected credential's bindings
/// follow its owner's next one. So naming an owner is acting on that person, under the rule every other act on a
/// member follows: they must be in this team, and below you unless they are you.
///
/// <para>Before this, an Admin could connect a token "owned" by the Owner and re-point the Owner's act-as identity at
/// it, plant identities on users of other tenants (who then saw them in their own list), and tell a real user id from
/// an invented one by a 200 against a foreign-key 500.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialOwnerReachTests
{
    private readonly PostgresFixture _fixture;

    public CredentialOwnerReachTests(PostgresFixture fixture) { _fixture = fixture; }

    public enum ConnectPath { Token, OAuth }

    public enum NamedOwner { AnotherTeamsUser, NoSuchUser, DepartedMember, TheOwner, AnotherAdmin, InstanceAdminOutsideTheTeam }

    [Theory]
    [InlineData(ConnectPath.Token, NamedOwner.AnotherTeamsUser)]
    [InlineData(ConnectPath.Token, NamedOwner.NoSuchUser)]
    [InlineData(ConnectPath.Token, NamedOwner.DepartedMember)]
    [InlineData(ConnectPath.Token, NamedOwner.TheOwner)]
    [InlineData(ConnectPath.Token, NamedOwner.AnotherAdmin)]
    [InlineData(ConnectPath.Token, NamedOwner.InstanceAdminOutsideTheTeam)]
    [InlineData(ConnectPath.OAuth, NamedOwner.AnotherTeamsUser)]
    [InlineData(ConnectPath.OAuth, NamedOwner.NoSuchUser)]
    [InlineData(ConnectPath.OAuth, NamedOwner.DepartedMember)]
    [InlineData(ConnectPath.OAuth, NamedOwner.TheOwner)]
    [InlineData(ConnectPath.OAuth, NamedOwner.AnotherAdmin)]
    [InlineData(ConnectPath.OAuth, NamedOwner.InstanceAdminOutsideTheTeam)]
    public async Task An_admin_cannot_connect_a_credential_in_the_name_of_someone_outside_their_reach(ConnectPath path, NamedOwner named)
    {
        var world = await SeedAsync();
        var ownerId = await NameAsync(world, named);

        var connect = () => StartConnectAsync(world, world.AdminId, path, ownerId);

        // An instance Admin stands in the team as an Owner does, row or not — so above a team Admin, not outside the team.
        if (named is NamedOwner.TheOwner or NamedOwner.AnotherAdmin or NamedOwner.InstanceAdminOutsideTheTeam)
            await connect.ShouldThrowAsync<RoleOutranksActorException>($"{path}: an Admin does not act on a {named}");
        else
            await connect.ShouldThrowAsync<KeyNotFoundException>($"{path}: a {named} is not in this team — and an id that exists elsewhere reads exactly like one that exists nowhere");

        (await CredentialsOwnedByAsync(world.TeamId, ownerId)).ShouldBe(0, $"{path}: a refused owner leaves no credential behind");
        (await PendingConnectsForAsync(world.TeamId, ownerId)).ShouldBe(0, $"{path}: a refused owner leaves no connect waiting for its callback");
    }

    [Theory]
    [InlineData(ConnectPath.Token, false)]
    [InlineData(ConnectPath.Token, true)]
    [InlineData(ConnectPath.OAuth, false)]
    [InlineData(ConnectPath.OAuth, true)]
    public async Task An_admin_still_connects_for_themselves_or_a_member_below_them(ConnectPath path, bool self)
    {
        var world = await SeedAsync();
        var ownerId = self ? world.AdminId : world.MemberId;

        await StartConnectAsync(world, world.AdminId, path, ownerId);

        var written = path == ConnectPath.Token ? await CredentialsOwnedByAsync(world.TeamId, ownerId) : await PendingConnectsForAsync(world.TeamId, ownerId);
        written.ShouldBe(1, $"{path}: naming {(self ? "yourself" : "a Member")} is within an Admin's reach");
    }

    [Theory]
    [InlineData(ConnectPath.Token, false)]
    [InlineData(ConnectPath.Token, true)]
    [InlineData(ConnectPath.OAuth, false)]
    [InlineData(ConnectPath.OAuth, true)]
    public async Task An_instance_admin_outside_the_team_connects_for_themselves(ConnectPath path, bool namesThemselves)
    {
        // A live instance Admin has the standing of an Owner in every team, membership row or not, as everywhere else in
        // CodeSpace — so the token they lend a team they never joined is theirs to lend, named or defaulted.
        var world = await SeedAsync();
        var instanceAdminId = await SeedInstanceAdminAsync();

        await StartConnectAsync(world, instanceAdminId, path, namesThemselves ? instanceAdminId : null, Roles.Admin);

        var written = path == ConnectPath.Token ? await CredentialsOwnedByAsync(world.TeamId, instanceAdminId) : await PendingConnectsForAsync(world.TeamId, instanceAdminId);
        written.ShouldBe(1, $"{path}: an instance Admin outside the team connects their own account");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_connect_someone_else_started_never_moves_the_owners_act_as_identity(bool ownerAlreadyLinked)
    {
        // The token an Admin connects "for" a member is whatever account the ADMIN signed into at the provider. Linking
        // it as the member's act-as identity would let the Admin choose who the member's writes are attributed to — so
        // only the person themselves, connecting, links or re-points their identity.
        var world = await SeedAsync();
        var linkedCredential = ownerAlreadyLinked ? await LinkOwnTokenAsync(world, world.MemberId) : (Guid?)null;

        var connected = await ConnectOAuthAsync(world, world.AdminId, world.MemberId);

        (await CredentialsOwnedByAsync(world.TeamId, world.MemberId)).ShouldBe(ownerAlreadyLinked ? 2 : 1, "fixture check: the Admin's connect wrote a credential in the member's name");

        var identity = await ResolveAsync(world.MemberId, world.InstanceId);

        if (!ownerAlreadyLinked)
        {
            identity.ShouldBeNull("nobody but the member creates the member's act-as identity");
            return;
        }

        identity.ShouldNotBeNull();
        identity!.CredentialId.ShouldBe(linkedCredential!.Value, "the member's identity still acts through the token THEY linked, not the one the Admin connected");
        identity.CredentialId.ShouldNotBe(connected);
    }

    // ── Flow steps ────────────────────────────────────────────────────────────────

    /// <summary>The first half of each connect path: a token added outright, or an OAuth connect started (Init) and waiting for its callback.</summary>
    private async Task StartConnectAsync(World world, Guid actorId, ConnectPath path, Guid? ownerId, params string[] roles)
    {
        using var scope = OAuthScope(actorId, world.TeamId, roles);
        var mediator = scope.Resolve<IMediator>();

        if (path == ConnectPath.Token)
        {
            await mediator.Send(new AddCredentialCommand { ProviderInstanceId = world.InstanceId, OwnerUserId = ownerId, DisplayName = "named token", Payload = new PatPayload { Token = "glpat-named" } });
            return;
        }

        await mediator.Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "named connect", IntendedOwnerUserId = ownerId });
    }

    private async Task<Guid> ConnectOAuthAsync(World world, Guid actorId, Guid ownerId)
    {
        InitCredentialOAuthResult init;

        using (var scope = OAuthScope(actorId, world.TeamId))
            init = await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "connected for someone", IntendedOwnerUserId = ownerId });

        using var callback = OAuthScope(userId: null, teamId: null);

        return (await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code", BrowserNonce = init.BrowserNonce })).CredentialId;
    }

    private async Task<Guid> LinkOwnTokenAsync(World world, Guid userId)
    {
        using (var scope = _fixture.BeginScopeAs(userId, world.TeamId))
            await scope.Resolve<IMediator>().Send(new LinkProviderIdentityByPatCommand { ProviderInstanceId = world.InstanceId, AccessToken = "members-own-token" });

        return (await ResolveAsync(userId, world.InstanceId))!.CredentialId;
    }

    private ILifetimeScope OAuthScope(Guid? userId, Guid? teamId, params string[] roles) =>
        _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", roles)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
            b.RegisterInstance<IOAuthClientRegistry>(new OneClientRegistry(new StubOAuthClient(ProviderKind.Git, new OAuthTokenResponse { AccessToken = "admins-account-token", RefreshToken = "rt" }))).SingleInstance();
        });

    private sealed class OneClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;

        public OneClientRegistry(IOAuthClient client) { _client = client; }

        public IOAuthClient Get(ProviderKind kind) => _client;
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────

    private async Task<UserProviderIdentity?> ResolveAsync(Guid userId, Guid instanceId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IActorIdentityResolver>().ResolveAsync(userId, instanceId, CancellationToken.None);
    }

    private async Task<int> CredentialsOwnedByAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().CountAsync(c => c.TeamId == teamId && c.OwnerUserId == ownerId);
    }

    private async Task<int> PendingConnectsForAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == teamId && s.IntendedOwnerUserId == ownerId);
    }

    // ── World ─────────────────────────────────────────────────────────────────────

    /// <summary>A team with an Owner, two Admins and a Member on one OAuth-capable Git instance, beside another team with a user of its own.</summary>
    private sealed record World(Guid TeamId, Guid InstanceId, Guid OwnerId, Guid AdminId, Guid PeerAdminId, Guid MemberId, Guid OtherTeamsUserId);

    private async Task<World> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"reach-{suffix}", Name = "Reach", Kind = TeamKind.Workspace };
        var otherTeam = new Team { Id = Guid.NewGuid(), Slug = $"other-{suffix}", Name = "Other", Kind = TeamKind.Workspace };
        var owner = NewUser("owner", suffix);
        var admin = NewUser("admin", suffix);
        var peerAdmin = NewUser("peer-admin", suffix);
        var member = NewUser("member", suffix);
        var otherTeamsUser = NewUser("elsewhere", suffix);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git-{suffix}.test", OauthClientId = "client", OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("secret") };

        db.Team.AddRange(team, otherTeam);
        db.User.AddRange(owner, admin, peerAdmin, member, otherTeamsUser);
        db.TeamMembership.AddRange(Membership(team, owner, TeamRole.Owner), Membership(team, admin, TeamRole.Admin), Membership(team, peerAdmin, TeamRole.Admin), Membership(team, member, TeamRole.Member), Membership(otherTeam, otherTeamsUser, TeamRole.Owner));
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return new World(team.Id, instance.Id, owner.Id, admin.Id, peerAdmin.Id, member.Id, otherTeamsUser.Id);
    }

    private async Task<Guid> NameAsync(World world, NamedOwner named) => named switch
    {
        NamedOwner.AnotherTeamsUser => world.OtherTeamsUserId,
        NamedOwner.NoSuchUser => Guid.NewGuid(),
        NamedOwner.DepartedMember => await RemoveTheMemberAsync(world),
        NamedOwner.TheOwner => world.OwnerId,
        NamedOwner.AnotherAdmin => world.PeerAdminId,
        NamedOwner.InstanceAdminOutsideTheTeam => await SeedInstanceAdminAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(named), named, "every named owner needs a seat in this world")
    };

    private async Task<Guid> RemoveTheMemberAsync(World world)
    {
        using var scope = _fixture.BeginScopeAs(world.OwnerId, world.TeamId);

        await scope.Resolve<IMediator>().Send(new RemoveTeamMemberCommand { UserId = world.MemberId });

        return world.MemberId;
    }

    /// <summary>An account holding the instance Admin role through a <c>role_user</c> row, and no membership anywhere.</summary>
    private async Task<Guid> SeedInstanceAdminAsync()
    {
        var admin = NewUser("instance-admin", Guid.NewGuid().ToString("N")[..8]);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(admin);
        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync(), UserId = admin.Id });
        await db.SaveChangesAsync();

        return admin.Id;
    }

    private static User NewUser(string label, string suffix) => new() { Id = Guid.NewGuid(), Email = $"{label}-{suffix}@reach.test", Name = $"{label}-{suffix}" };

    private static TeamMembership Membership(Team team, User user, TeamRole role) => new() { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = role };
}
