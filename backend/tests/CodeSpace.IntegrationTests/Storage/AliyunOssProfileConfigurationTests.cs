using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Storage;

/// <summary>
/// Proves the new provider is genuinely CONFIGURABLE: an operator can persist an Aliyun OSS credential and profile
/// through the real control-plane services, and the runtime broker can open that exact revision into a real driver -
/// with the secret encrypted at rest, absent from every read model, and never outliving the activation lease.
/// No route or data class is bound here, so no existing byte moves.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AliyunOssProfileConfigurationTests
{
    private const string AccessKeySecret = "wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET";
    private readonly PostgresFixture _fixture;

    public AliyunOssProfileConfigurationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_operator_configured_oss_profile_opens_into_a_real_driver_without_exposing_its_secret()
    {
        var world = await SeedWorldAsync();
        var (profileId, revision) = await ConfigureAsync(world);

        using var scope = _fixture.BeginScope();
        var resolution = await scope.Resolve<IStorageRuntimeDriverBroker>()
            .OpenAsync(new StorageRuntimeDriverRequest(world.TeamId, profileId, revision, StorageProfileEligibility.Write), CancellationToken.None);

        var ready = resolution.ShouldBeOfType<StorageRuntimeDriverResolution.Ready>();
        await using var lease = ready.Lease;
        lease.Driver.ShouldBeOfType<AliyunOssArtifactStorageDriver>();
        lease.Driver.ToString().ShouldNotContain(AccessKeySecret);
    }

    [Fact]
    public async Task The_persisted_profile_and_credential_never_hand_the_secret_back_to_a_reader()
    {
        var world = await SeedWorldAsync();
        var (profileId, _) = await ConfigureAsync(world);

        using var scope = _fixture.BeginScope();
        var profile = await scope.Resolve<IStorageProfileService>().GetAsync(world.TeamId, profileId, CancellationToken.None);
        var credentials = await scope.Resolve<IStorageCredentialService>().ListAsync(world.TeamId, CancellationToken.None);

        JsonSerializer.Serialize(profile).ShouldNotContain(AccessKeySecret);
        JsonSerializer.Serialize(credentials).ShouldNotContain(AccessKeySecret);
        profile!.Revisions.Single().NonSecretConfig.GetProperty("bucket").GetString().ShouldBe("codespace-artifacts");
        profile.Revisions.Single().NonSecretConfig.TryGetProperty("accessKeySecret", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_profile_that_smuggles_the_access_key_into_its_configuration_is_refused()
    {
        var world = await SeedWorldAsync();

        using var scope = _fixture.BeginScope();
        var command = new CreateStorageProfileCommand
        {
            StableName = "oss-smuggled",
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonSerializer.SerializeToElement(new
            {
                endpoint = "oss-cn-hangzhou.aliyuncs.com",
                region = "cn-hangzhou",
                bucket = "codespace-artifacts",
                accessKeySecret = AccessKeySecret
            })
        };

        var error = await Should.ThrowAsync<ArgumentException>(() => scope.Resolve<IStorageProfileService>().CreateAsync(world.TeamId, world.ActorId, command, CancellationToken.None));

        error.Message.ShouldNotContain(AccessKeySecret);
    }

    private async Task<(Guid ProfileId, int Revision)> ConfigureAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var credential = await scope.Resolve<IStorageCredentialService>().CreateAsync(world.TeamId, world.ActorId, new CreateStorageCredentialCommand
        {
            StableName = "oss-artifacts",
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            Secret = JsonSerializer.SerializeToElement(new { accessKeyId = "LTAI5tFakeAccessKeyId", accessKeySecret = AccessKeySecret }),
            SafeHint = "LTAI…KeyId"
        }, CancellationToken.None);

        var profiles = scope.Resolve<IStorageProfileService>();
        var created = await profiles.CreateAsync(world.TeamId, world.ActorId, new CreateStorageProfileCommand
        {
            StableName = "oss-artifacts",
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonSerializer.SerializeToElement(new
            {
                endpoint = "oss-cn-hangzhou.aliyuncs.com",
                region = "cn-hangzhou",
                bucket = "codespace-artifacts",
                keyPrefix = "team-artifacts/"
            }),
            CredentialRef = $"db:{credential.Id:D}:{credential.CurrentRevision}"
        }, CancellationToken.None);

        var activated = await profiles.SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = created.Id,
            ExpectedXmin = created.Xmin,
            ExpectedCurrentRevision = created.CurrentRevision,
            State = StorageProfileStateValue.Active
        }, CancellationToken.None);

        activated!.State.ShouldBe(StorageProfileStateValue.Active);
        return (created.Id, created.CurrentRevision);
    }

    private async Task<World> SeedWorldAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = actorId, Email = $"oss-config-{actorId:N}@test.local", Name = "Aliyun OSS config" });
        db.Team.Add(new Team { Id = teamId, Slug = $"oss-config-{teamId:N}", Name = "Aliyun OSS config", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return new World(teamId, actorId);
    }

    private sealed record World(Guid TeamId, Guid ActorId);
}
