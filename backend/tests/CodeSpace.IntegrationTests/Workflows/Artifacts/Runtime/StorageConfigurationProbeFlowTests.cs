using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The claim this seam is worth having for, asserted against a real database: qualifying a destination writes nothing
/// to the control plane, whether the destination answered or refused.
///
/// <para>A unit test cannot make this claim. It can show the service holds no <c>DbContext</c>, but the command runs
/// inside <c>TransactionalBehavior</c> alongside the whole MediatR pipeline, and it is the pipeline as a whole that
/// must leave no row - a probe recorded by a decorator, an audit row, a health row keyed off an id that exists
/// nowhere. The cost of getting this wrong is not a stale row: <c>storage_profile</c> has no delete, so a row written
/// here is one an operator can never remove, which is exactly the outcome this seam exists to prevent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageConfigurationProbeFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly FakeAliyunOssHandler _oss = new();

    public StorageConfigurationProbeFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false, StorageProfileProbeStatusValue.Available)]
    [InlineData(true, StorageProfileProbeStatusValue.Unavailable)]
    public async Task Qualifying_a_destination_leaves_no_control_plane_row_whether_it_answers_or_refuses(bool wrongSecret, StorageProfileProbeStatusValue expected)
    {
        var world = await SeedTeamAsync();
        _oss.RejectEverySignature = wrongSecret;

        var result = await ProbeAsync(world);

        result.Status.ShouldBe(expected, result.Failure?.Code.ToString());
        if (wrongSecret) result.Failure.ShouldNotBeNull().Code.ShouldBe(StorageProfileProbeFailureCodeValue.ProbeSignatureMismatch);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.StorageProfile.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0, "a profile written here could never be removed again");
        (await db.StorageProfileRevision.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0);
        (await db.StorageCredential.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0, "and a credential written here would be one more thing to revoke");
        (await db.StorageRoute.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0);
        (await db.StorageProfileHealth.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(0, "there is no profile for a health row to describe");
    }

    /// <summary>
    /// The negative control for the assertions above: the same fixture, the same team, driven through the ordinary
    /// save path, DOES leave rows. Without it, a probe that silently did nothing at all - never reaching the provider,
    /// never answering - would satisfy every count above.
    /// </summary>
    [Fact]
    public async Task The_ordinary_save_path_on_the_same_team_does_leave_rows()
    {
        var world = await SeedTeamAsync();

        using (var scope = _fixture.BeginScopeAs(world.ActorId, world.TeamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new CreateStorageCredentialCommand
            {
                StableName = $"probe-control-{Guid.NewGuid():N}",
                ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
                Secret = Secret(),
            });
        }

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().StorageCredential.CountAsync(row => row.TeamId == world.TeamId)).ShouldBe(1);
    }

    public void Dispose() => _oss.Dispose();

    private async Task<StorageConfigurationProbeResult> ProbeAsync(World world)
    {
        using var scope = _fixture.BeginScopeAs(world.ActorId, world.TeamId, Roles.Admin);

        // Only the HTTP transport is substituted; the module catalog, the factory, the activator, the probe service
        // and the whole MediatR pipeline are the registered production ones.
        using var probe = scope.BeginLifetimeScope(builder => builder
            .RegisterInstance<IArtifactStorageDriverFactoryCatalog>(new ArtifactStorageDriverFactoryCatalog(
                [new AliyunOssArtifactStorageDriverFactory(_oss)],
                new StorageProviderModuleCatalog([new AliyunOssStorageProviderModule()])))
            .SingleInstance());

        return await probe.Resolve<IMediator>().Send(new ProbeStorageConfigurationCommand
        {
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonSerializer.SerializeToElement(new
            {
                endpoint = FakeAliyunOssHandler.Host,
                region = FakeAliyunOssHandler.Region,
                bucket = FakeAliyunOssHandler.Bucket,
                keyPrefix = "codespace/",
            }),
            Secret = Secret(),
        });
    }

    private static JsonElement Secret() => JsonSerializer.SerializeToElement(new
    {
        accessKeyId = FakeAliyunOssHandler.AccessKeyId,
        accessKeySecret = FakeAliyunOssHandler.AccessKeySecret,
        securityToken = FakeAliyunOssHandler.SecurityToken,
    });

    private async Task<World> SeedTeamAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"probe-{actorId:N}@test.local", Name = $"probe-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"probe-{teamId:N}", Name = "Storage Probe Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        return new World(actorId, teamId);
    }

    private sealed record World(Guid ActorId, Guid TeamId);
}
