using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Identity;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// Full-pipeline coverage for the <c>/api/me/identities</c> surface: link (PAT) → list → unlink,
/// driven through MediatR so the command/query handlers, the team-membership auth behaviour, the
/// real <c>IProviderRegistry</c>, and the service all run together. The provider whoami comes from
/// the test-only <c>ProviderKind.Git</c> double (<c>TestRepositoryProvider</c>), so no real Git host
/// is contacted. Proves the wiring the isolated service tests don't exercise.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MeIdentitiesApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public MeIdentitiesApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Link_then_list_then_unlink_round_trips_through_the_pipeline()
    {
        var (userId, teamId, instanceId) = await SeedGitInstanceAsync();

        UserProviderIdentitySummary linked;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            linked = await scope.Resolve<IMediator>().Send(new LinkProviderIdentityByPatCommand { ProviderInstanceId = instanceId, AccessToken = "glpat-abc" });

        // TestRepositoryProvider's canned whoami flows all the way back through the handler.
        linked.ProviderUsername.ShouldBe("Test User");
        linked.ProviderUserId.ShouldBe("test-user-id");
        linked.Provider.ShouldBe(ProviderKind.Git);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var list = await scope.Resolve<IMediator>().Send(new ListMyProviderIdentitiesQuery());
            list.ShouldContain(i => i.Id == linked.Id);
        }

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new UnlinkProviderIdentityCommand { IdentityId = linked.Id });

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var afterUnlink = await scope.Resolve<IMediator>().Send(new ListMyProviderIdentitiesQuery());
            afterUnlink.ShouldNotContain(i => i.Id == linked.Id, "an unlinked identity must drop out of the caller's list");
        }
    }

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedGitInstanceAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.Git,
            DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local",
            OauthClientId = "client",
            OauthClientSecretEnc = encryptor.Encrypt("secret")
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return (user.Id, team.Id, instance.Id);
    }
}
