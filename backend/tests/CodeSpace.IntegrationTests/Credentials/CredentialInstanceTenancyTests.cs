using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Pins the credential ↔ provider-instance ↔ team consistency invariant for the paths that take a
/// <c>ProviderInstanceId</c> / <c>CredentialId</c> from the request body. The existing
/// <c>TenancyEnforcementTests</c> only cover HEADER spoofing (a member of team A sends
/// <c>X-Team-Id: B</c>, tripped by the membership behavior). These cover the residual gap that gate
/// can't see: a caller acting in their OWN valid team who passes a FOREIGN or MISMATCHED instance id.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialInstanceTenancyTests
{
    private readonly PostgresFixture _fixture;

    public CredentialInstanceTenancyTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task AddCredential_with_foreign_team_provider_instance_is_rejected()
    {
        var (userA, teamA) = await SeedTeamAsync().ConfigureAwait(false);
        var (_, teamB) = await SeedTeamAsync().ConfigureAwait(false);
        var foreignInstance = await AddInstanceAsync(teamB).ConfigureAwait(false);

        // userA acts in their OWN team (valid X-Team-Id: teamA) but passes teamB's instance id.
        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new AddCredentialCommand
        {
            ProviderInstanceId = foreignInstance,
            DisplayName = "x",
            Payload = new PatPayload { Token = "t" }
        }).ConfigureAwait(false);

        await act.ShouldThrowAsync<KeyNotFoundException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task AddCredential_with_own_team_provider_instance_succeeds()
    {
        var (userA, teamA) = await SeedTeamAsync().ConfigureAwait(false);
        var instance = await AddInstanceAsync(teamA).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var credentialId = await mediator.Send(new AddCredentialCommand
        {
            ProviderInstanceId = instance,
            DisplayName = "ok",
            Payload = new PatPayload { Token = "t" }
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var saved = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        saved.TeamId.ShouldBe(teamA);
        saved.ProviderInstanceId.ShouldBe(instance);
    }

    [Fact]
    public async Task Bind_with_credential_of_a_different_instance_in_same_team_is_rejected()
    {
        var (userA, teamA) = await SeedTeamAsync().ConfigureAwait(false);
        var instance1 = await AddInstanceAsync(teamA).ConfigureAwait(false);
        var instance2 = await AddInstanceAsync(teamA).ConfigureAwait(false);
        var credentialOnInstance2 = await AddCredentialAsync(teamA, instance2).ConfigureAwait(false);

        // instance1 + the credential are both in teamA (team scope passes), but the credential belongs to
        // instance2 — binding it against instance1 must be rejected BEFORE any provider wire call.
        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = instance1,
            CredentialId = credentialOnInstance2,
            ProjectIdentifier = "group/repo"
        }).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        ex.Message.ShouldContain("different provider instance");
    }

    // ── Seed helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, user.Id);

        db.User.Add(user);
        db.Team.Add(team);
        db.Project.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id);
    }

    private async Task<Guid> AddInstanceAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.Git,
            DisplayName = "instance",
            BaseUrl = $"https://{Guid.NewGuid():N}.local"
        };
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return instance.Id;
    }

    private async Task<Guid> AddCredentialAsync(Guid teamId, Guid providerInstanceId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = providerInstanceId,
            AuthType = AuthType.Pat,
            DisplayName = "pat",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "secret" }))
        };
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }
}
