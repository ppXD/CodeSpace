using CodeSpace.Messages.Failures;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
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

    /// <summary>
    /// Both shapes of the same profile open into a driver: the one that states its region and the one that leaves the
    /// region to the endpoint. The region-less case is the one that proves the provider is configurable without a
    /// value the operator does not hold, and it has to pass through the REAL control plane - schema admission,
    /// canonicalization, persistence, activation - not just the target parser.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_configured_oss_profile_opens_into_a_real_driver_without_exposing_its_secret(bool withExplicitRegion)
    {
        var world = await SeedWorldAsync();
        var (profileId, revision) = await ConfigureAsync(world, withExplicitRegion);

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
        var (profileId, _) = await ConfigureAsync(world, withExplicitRegion: true);

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

        // The refusal is the platform's TYPED invalid-configuration failure, not a bare argument check: the control
        // plane maps it to a client-facing code, so pinning the type and the code is what keeps a secret-bearing
        // config rejected THROUGH the API rather than merely rejected somewhere.
        var error = await Should.ThrowAsync<StorageProfileInvalidException>(() => scope.Resolve<IStorageProfileService>().CreateAsync(world.TeamId, world.ActorId, command, CancellationToken.None));

        error.Code.ShouldBe(FailureCodes.StorageProfileInvalid);
        error.Kind.ShouldBe(FailureKind.Invalid);
        error.Message.ShouldNotContain(AccessKeySecret, customMessage: "a refusal must name the offending property, never echo the value it refused");
        error.ClientMessage.ShouldNotContain(AccessKeySecret, customMessage: "the client-facing text is what actually reaches an operator's screen");
    }

    /// <summary>
    /// The schema admits an accelerate endpoint - it is a legal host - and only the parser knows that no region can be
    /// read from it. So this is the assertion that the refusal reaches the operator where they are standing: nothing is
    /// stored, and the text names the field to fill. Without it the profile saves, activates, and fails at its first
    /// artifact write, which happens mid-run.
    /// </summary>
    [Fact]
    public async Task A_profile_whose_endpoint_names_no_region_is_refused_as_it_is_saved_and_leaves_nothing_stored()
    {
        var world = await SeedWorldAsync();

        using var scope = _fixture.BeginScope();
        var profiles = scope.Resolve<IStorageProfileService>();
        var command = new CreateStorageProfileCommand
        {
            StableName = "oss-accelerate",
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonSerializer.SerializeToElement(new { endpoint = "oss-accelerate.aliyuncs.com", bucket = "codespace-artifacts" })
        };

        var error = await Should.ThrowAsync<StorageProfileInvalidException>(() => profiles.CreateAsync(world.TeamId, world.ActorId, command, CancellationToken.None));

        error.Code.ShouldBe(FailureCodes.StorageProfileInvalid);
        error.Kind.ShouldBe(FailureKind.Invalid);
        error.ClientMessage.ShouldNotBeNull().ShouldContain("'region'", Case.Sensitive, "the text that reaches an operator's screen has to name the field to fill, not merely report that the profile is invalid");
        error.ClientMessage.ShouldContain("oss-accelerate.aliyuncs.com", Case.Sensitive, "naming the host that could not be read is what stops the operator hunting through the other fields");
        error.ClientMessage.ShouldContain("cn-hangzhou", Case.Sensitive, "an example region id tells the operator what shape of value to supply");
        (await profiles.ListAsync(world.TeamId, CancellationToken.None)).ShouldBeEmpty("a profile whose driver could never open it must not survive the refusal");
    }

    /// <summary>
    /// Appending a revision is the second way the same configuration reaches storage, and revisions are append-only: an
    /// unopenable one admitted here would become the profile's current revision and break a profile that works today.
    /// </summary>
    [Fact]
    public async Task An_appended_revision_whose_endpoint_names_no_region_is_refused_and_leaves_the_working_revision_current()
    {
        var world = await SeedWorldAsync();
        var (profileId, revision) = await ConfigureAsync(world, withExplicitRegion: false);

        using var scope = _fixture.BeginScope();
        var profiles = scope.Resolve<IStorageProfileService>();
        var current = await profiles.GetAsync(world.TeamId, profileId, CancellationToken.None);
        var command = new AppendStorageProfileRevisionCommand
        {
            ProfileId = profileId,
            ExpectedXmin = current!.Xmin,
            ExpectedCurrentRevision = revision,
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonSerializer.SerializeToElement(new { endpoint = "artifacts.example.com", bucket = "codespace-artifacts" })
        };

        var error = await Should.ThrowAsync<StorageProfileInvalidException>(() => profiles.AppendRevisionAsync(world.TeamId, world.ActorId, command, CancellationToken.None));

        error.ClientMessage.ShouldNotBeNull().ShouldContain("'region'", Case.Sensitive);
        var unchanged = await profiles.GetAsync(world.TeamId, profileId, CancellationToken.None);
        unchanged!.CurrentRevision.ShouldBe(revision);
        unchanged.Revisions.Single().NonSecretConfig.GetProperty("endpoint").GetString().ShouldBe("oss-cn-hangzhou.aliyuncs.com");
    }

    private async Task<(Guid ProfileId, int Revision)> ConfigureAsync(World world, bool withExplicitRegion)
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
            NonSecretConfig = NonSecretConfig(withExplicitRegion),
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

    /// <summary>The four-value profile, plus the region override only when the case under test states one.</summary>
    private static JsonElement NonSecretConfig(bool withExplicitRegion)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["endpoint"] = "oss-cn-hangzhou.aliyuncs.com",
            ["bucket"] = "codespace-artifacts",
            ["keyPrefix"] = "team-artifacts/"
        };
        if (withExplicitRegion) config["region"] = "cn-hangzhou";

        return JsonSerializer.SerializeToElement(config);
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
