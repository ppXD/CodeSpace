using System.Text.Json;
using CodeSpace.Core.Handlers.CommandHandlers.Storage;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

[Trait("Category", "Unit")]
public sealed class StorageProfileProbeServiceTests
{
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _profileId = Guid.NewGuid();

    [Fact]
    public async Task Command_is_admin_only_and_handler_uses_the_current_team()
    {
        var broker = new StubBroker(new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.NotActive));
        var service = new StorageProfileProbeService(new StubTargetResolver(Target(2)), broker);
        var handler = new ProbeStorageProfileCommandHandler(service, new StubCurrentTeam(_teamId));
        var command = new ProbeStorageProfileCommand { ProfileId = _profileId, ProfileRevision = 2 };

        command.ShouldBeAssignableTo<IRequireTeamPermission>();
        command.RequiredPermission.ShouldBe(CodeSpace.Messages.Constants.TeamPermissions.StorageManage);
        var result = await handler.Handle(command, CancellationToken.None);

        broker.Request.ShouldBe(new StorageRuntimeDriverRequest(_teamId, _profileId, 2, StorageProfileEligibility.Write));
        result.ProfileRevision.ShouldBe(2);
        result.Failure!.Code.ShouldBe(StorageProfileProbeFailureCodeValue.ProfileNotActive);
    }

    [Fact]
    public async Task Omitted_revision_uses_the_profiles_exact_current_revision()
    {
        var broker = new StubBroker(new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.RevisionMissing));
        var service = new StorageProfileProbeService(new StubTargetResolver(Target(7)), broker);

        var result = await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, null, true), CancellationToken.None);

        broker.Request.ShouldBe(new StorageRuntimeDriverRequest(_teamId, _profileId, 7, StorageProfileEligibility.Write));
        result.ProfileRevision.ShouldBe(7);
        result.WriteAccessRequested.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, StorageProfileEligibility.Write, true)]
    [InlineData(false, StorageProfileEligibility.Read, false)]
    public async Task What_the_probe_asks_the_profile_for_and_whether_it_may_provision_both_follow_what_it_verifies(bool verifyWriteAccess, StorageProfileEligibility expected, bool mayProvision)
    {
        // Hardcoding Write made a probe of a Disabled or Retired profile a restatement of storage_profile.state:
        // the lifecycle gate refused it before any driver opened, so nothing ever contacted the destination that
        // every one of its stored objects still lives on. That same gate was also the only thing stopping a read
        // from PROVISIONING one, so the rule it enforced by accident is now stated: you may only create a
        // destination you are about to prove you can write to.
        var driver = new StubDriver();
        var broker = new StubBroker(new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver)));
        var service = new StorageProfileProbeService(new StubTargetResolver(Target(4)), broker);

        await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 4, verifyWriteAccess, Initialize: true), CancellationToken.None);

        broker.Request.ShouldBe(new StorageRuntimeDriverRequest(_teamId, _profileId, 4, expected));
        driver.Request.ShouldNotBeNull().Initialize.ShouldBe(mayProvision, "a probe that is not about to write must report an absent destination, never create it");
    }

    [Fact]
    public async Task Probe_error_is_typed_without_provider_message_code_configuration_or_credential()
    {
        const string secret = "provider-secret-must-not-cross-wire";
        var driver = new StubDriver
        {
            Probe = new ArtifactStorageProbeResult
            {
                Status = ArtifactStorageProbeStatus.Unavailable,
                Latency = TimeSpan.FromMilliseconds(9),
                Error = new ArtifactStorageError(ArtifactStorageErrorCode.Throttled, secret, true, "vendor-secret-code"),
            },
        };
        var broker = new StubBroker(new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver)));
        var service = new StorageProfileProbeService(new StubTargetResolver(Target(1)), broker);

        var result = await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 1, true), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable);
        result.Failure.ShouldBe(new StorageProfileProbeFailure
        {
            Stage = StorageProfileProbeFailureStageValue.Probe,
            Code = StorageProfileProbeFailureCodeValue.ProbeThrottled,
            Retryable = true,
        });
        driver.DisposeCount.ShouldBe(1);
        var wire = JsonSerializer.Serialize(result);
        wire.ShouldNotContain(secret);
        wire.ShouldNotContain("vendor-secret-code");
        wire.ShouldNotContain("credential", Case.Insensitive);
        wire.ShouldNotContain("config", Case.Insensitive);
    }

    [Theory]
    [InlineData(ArtifactStorageFailureReason.CredentialInvalid, StorageProfileProbeFailureCodeValue.ProbeCredentialInvalid)]
    [InlineData(ArtifactStorageFailureReason.SignatureMismatch, StorageProfileProbeFailureCodeValue.ProbeSignatureMismatch)]
    [InlineData(ArtifactStorageFailureReason.SecurityTokenInvalid, StorageProfileProbeFailureCodeValue.ProbeSecurityTokenInvalid)]
    [InlineData(ArtifactStorageFailureReason.SecurityTokenExpired, StorageProfileProbeFailureCodeValue.ProbeSecurityTokenExpired)]
    [InlineData(ArtifactStorageFailureReason.SecurityTokenMissing, StorageProfileProbeFailureCodeValue.ProbeSecurityTokenMissing)]
    [InlineData(ArtifactStorageFailureReason.ClockSkew, StorageProfileProbeFailureCodeValue.ProbeClockSkew)]
    [InlineData(ArtifactStorageFailureReason.DestinationMissing, StorageProfileProbeFailureCodeValue.ProbeDestinationMissing)]
    [InlineData(ArtifactStorageFailureReason.PermissionDenied, StorageProfileProbeFailureCodeValue.ProbePermissionDenied)]
    [InlineData(ArtifactStorageFailureReason.NetworkUnavailable, StorageProfileProbeFailureCodeValue.ProbeNetworkUnavailable)]
    public async Task Safe_provider_neutral_reasons_are_preserved_as_actionable_probe_codes(ArtifactStorageFailureReason reason, StorageProfileProbeFailureCodeValue expected)
    {
        var driver = new StubDriver
        {
            Probe = new ArtifactStorageProbeResult
            {
                Status = ArtifactStorageProbeStatus.Unavailable,
                Latency = TimeSpan.Zero,
                Error = new ArtifactStorageError(ArtifactStorageErrorCode.Unauthorized, "redacted") { Reason = reason }
            }
        };
        var service = new StorageProfileProbeService(new StubTargetResolver(Target(1)), new StubBroker(new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver))));

        var result = await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 1, true), CancellationToken.None);

        result.Failure!.Code.ShouldBe(expected);
    }

    [Fact]
    public async Task Provider_exception_and_cleanup_failure_become_secret_free_typed_failures()
    {
        var driver = new StubDriver { ProbeException = new IOException("secret-provider-path"), DisposeException = new IOException("secret-cleanup-path") };
        var service = new StorageProfileProbeService(
            new StubTargetResolver(Target(1)),
            new StubBroker(new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver))));

        var result = await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 1, false), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable);
        result.Failure!.Stage.ShouldBe(StorageProfileProbeFailureStageValue.DriverCleanup);
        result.Failure.Code.ShouldBe(StorageProfileProbeFailureCodeValue.DriverCleanupFailure);
        result.Failure.Retryable.ShouldBeTrue();
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
    }

    [Fact]
    public async Task Contradictory_available_with_error_is_sanitized_fail_closed()
    {
        var driver = new StubDriver
        {
            Probe = new ArtifactStorageProbeResult
            {
                Status = ArtifactStorageProbeStatus.Available,
                Latency = TimeSpan.Zero,
                Error = new ArtifactStorageError(ArtifactStorageErrorCode.Throttled, "provider detail", true),
            },
        };
        var service = new StorageProfileProbeService(
            new StubTargetResolver(Target(1)),
            new StubBroker(new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver))));

        var result = await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 1, true), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Degraded);
        result.Failure!.Code.ShouldBe(StorageProfileProbeFailureCodeValue.ProbeThrottled);
    }

    [Fact]
    public async Task Fatal_provider_failure_is_not_reclassified_and_the_ready_lease_is_still_disposed()
    {
        var driver = new StubDriver { ProbeException = new OutOfMemoryException("fatal") };
        var service = new StorageProfileProbeService(
            new StubTargetResolver(Target(1)),
            new StubBroker(new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver))));

        await Should.ThrowAsync<OutOfMemoryException>(() => service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 1, true), CancellationToken.None));

        driver.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(StorageRuntimeCredentialFailureReason.InvalidEnvelope, StorageProfileProbeFailureCodeValue.CredentialEnvelopeInvalid)]
    [InlineData(StorageRuntimeCredentialFailureReason.ProviderMismatch, StorageProfileProbeFailureCodeValue.CredentialProviderMismatch)]
    [InlineData(StorageRuntimeCredentialFailureReason.ResolutionFailed, StorageProfileProbeFailureCodeValue.CredentialResolutionFailed)]
    public async Task Broker_failures_remain_precisely_typed(StorageRuntimeCredentialFailureReason reason, StorageProfileProbeFailureCodeValue expected)
    {
        var service = new StorageProfileProbeService(
            new StubTargetResolver(Target(1)),
            new StubBroker(new StorageRuntimeDriverResolution.CredentialUnavailable(reason)));

        var result = await service.ProbeAsync(new StorageProfileProbeRequest(_teamId, _profileId, 1, true), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable);
        result.Failure!.Stage.ShouldBe(StorageProfileProbeFailureStageValue.Credential);
        result.Failure.Code.ShouldBe(expected);
    }

    private StorageProfileProbeTarget Target(int revision) => new(_profileId, revision, "local-rwx/v1");

    private sealed class StubBroker(StorageRuntimeDriverResolution resolution) : IStorageRuntimeDriverBroker
    {
        public StorageRuntimeDriverRequest? Request { get; private set; }
        public ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(resolution);
        }
    }

    private sealed class StubTargetResolver(StorageProfileProbeTarget? target) : IStorageProfileProbeTargetResolver
    {
        public Task<StorageProfileProbeTarget?> ResolveAsync(StorageProfileProbeTargetRequest request, CancellationToken cancellationToken) => Task.FromResult(target);
    }

    private sealed class StubDriver : IArtifactStorageDriver
    {
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public ArtifactStorageProbeResult Probe { get; init; } = new() { Status = ArtifactStorageProbeStatus.Available, Latency = TimeSpan.Zero };
        public Exception? ProbeException { get; init; }
        public Exception? DisposeException { get; init; }
        public int DisposeCount { get; private set; }
        public ArtifactStorageProbeRequest? Request { get; private set; }

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            if (ProbeException != null) throw ProbeException;
            return ValueTask.FromResult(Probe);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeException == null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeException);
        }

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam(Guid id) : ICurrentTeam
    {
        public Guid? Id { get; } = id;
        public bool IsSet => true;
    }
}
