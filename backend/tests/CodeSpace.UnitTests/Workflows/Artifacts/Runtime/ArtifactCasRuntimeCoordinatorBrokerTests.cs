using System.Security.Cryptography;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

[Trait("Category", "Unit")]
public sealed class ArtifactCasRuntimeCoordinatorBrokerTests
{
    public static TheoryData<StorageRuntimeDriverResolution, ArtifactCasProblem> BrokerFailures => new()
    {
        { new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.Missing), Problem(ArtifactCasProblemCode.ProfileMissing) },
        { new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.NotActive), Problem(ArtifactCasProblemCode.ProfileNotActive) },
        { new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.RevisionMissing), Problem(ArtifactCasProblemCode.ProfileRevisionMissing) },
        { new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed), Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.Missing), Problem(ArtifactCasProblemCode.CredentialUnavailable) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.NotActive), Problem(ArtifactCasProblemCode.CredentialUnavailable) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.RevisionMissing), Problem(ArtifactCasProblemCode.CredentialUnavailable) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ProviderMismatch), Problem(ArtifactCasProblemCode.CredentialInvalid) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ProviderUnavailable), Problem(ArtifactCasProblemCode.CredentialBrokerUnavailable, true) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidEnvelope), Problem(ArtifactCasProblemCode.CredentialInvalid) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidReference), Problem(ArtifactCasProblemCode.CredentialInvalid) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidSecret), Problem(ArtifactCasProblemCode.CredentialInvalid) },
        { new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ResolutionFailed), Problem(ArtifactCasProblemCode.CredentialBrokerUnavailable, true) },
        { new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.ModuleMissing), Problem(ArtifactCasProblemCode.ProviderUnavailable) },
        { new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.FactoryMissing), Problem(ArtifactCasProblemCode.ProviderUnavailable) },
        { new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.FactoryMismatch), Problem(ArtifactCasProblemCode.ProviderUnavailable) },
        { new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.CatalogFailure), Problem(ArtifactCasProblemCode.ProviderFailure, true) },
        { new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.InvalidConfiguration), Problem(ArtifactCasProblemCode.ProfileInvalid) },
        { new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.UnsupportedSchemaVersion), Problem(ArtifactCasProblemCode.ProfileInvalid) },
        { new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch), Problem(ArtifactCasProblemCode.ProfileInvalid) },
        { new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.InvalidProviderTypeKey), Problem(ArtifactCasProblemCode.ProfileInvalid) },
        { new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.FactoryRejectedConfiguration), Problem(ArtifactCasProblemCode.Unsupported) },
        { new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.ProfileResolution), Problem(ArtifactCasProblemCode.ProviderTimeout, true) },
        { new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.CredentialResolution), Problem(ArtifactCasProblemCode.ProviderTimeout, true) },
        { new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization), Problem(ArtifactCasProblemCode.ProviderTimeout, true) },
        { new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.NullDriver), Problem(ArtifactCasProblemCode.ProviderFailure, true) },
        { new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.ProviderCanceled), Problem(ArtifactCasProblemCode.ProviderTimeout, true) },
        { new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.ProviderFailure), Problem(ArtifactCasProblemCode.ProviderFailure, true) },
        { new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.CleanupFailure), Problem(ArtifactCasProblemCode.ProviderFailure, true) },
    };

    [Theory]
    [MemberData(nameof(BrokerFailures))]
    public void Every_broker_failure_maps_to_a_closed_secret_free_CAS_problem(StorageRuntimeDriverResolution resolution, ArtifactCasProblem expected)
    {
        var actual = ArtifactCasRuntimeCoordinator.MapBrokerFailure(resolution);

        actual.ShouldBe(expected);
        actual.ToString().ShouldNotContain("Reason", Case.Sensitive);
    }

    [Fact]
    public async Task Mapping_inventory_covers_every_declared_broker_failure_reason_and_fails_closed_for_ready_misuse()
    {
        var declaredReasonCount = Enum.GetValues<StorageRuntimeProfileFailureReason>().Length
            + Enum.GetValues<StorageRuntimeCredentialFailureReason>().Length
            + Enum.GetValues<StorageRuntimeProviderFailureReason>().Length
            + Enum.GetValues<StorageRuntimeConfigurationFailureReason>().Length
            + Enum.GetValues<StorageRuntimeCancellationStage>().Length
            + Enum.GetValues<StorageRuntimeDriverInitializationFailureReason>().Length;

        BrokerFailures.Count().ShouldBe(declaredReasonCount);
        var driver = new CapabilityDriver(StorageProviderCapabilities.None);
        var lease = new StorageRuntimeDriverLease(driver);
        ArtifactCasRuntimeCoordinator.MapBrokerFailure(new StorageRuntimeDriverResolution.Ready(lease)).ShouldBe(Problem(ArtifactCasProblemCode.ProviderFailure));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Capability_mismatch_disposes_the_single_broker_lease_exactly_once()
    {
        var driver = new CapabilityDriver(StorageProviderCapabilities.StreamingRead);
        var lease = new StorageRuntimeDriverLease(driver);

        var problem = await ArtifactCasRuntimeCoordinator.RequireCapabilitiesAsync(lease, StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.StreamingWrite);

        problem.ShouldBe(Problem(ArtifactCasProblemCode.Unsupported));
        driver.DisposeCalls.ShouldBe(1);
        await lease.DisposeAsync();
        driver.DisposeCalls.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => _ = lease.Driver);
    }

    [Fact]
    public async Task Unified_lease_returns_promptly_but_never_disposes_a_driver_concurrently_with_an_abandoned_operation()
    {
        var driver = new CapabilityDriver(StorageProviderCapabilities.StreamingRead);
        var lease = new StorageRuntimeDriverLease(driver);
        var pending = new TaskCompletionSource<ArtifactStorageHeadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = lease.Track(pending.Task);
        lease.Abandon(pending.Task);

        var disposal = lease.DisposeAsync();

        disposal.IsCompletedSuccessfully.ShouldBeTrue();
        driver.DisposeCalls.ShouldBe(0);
        pending.SetResult(ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "typed-only")));
        await driver.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        driver.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Unified_lease_dynamically_cleans_a_late_read_result_before_driver_disposal_when_dispose_wins_the_race()
    {
        var content = new DisposalTrackingStream([42]);
        var driver = new CapabilityDriver(StorageProviderCapabilities.StreamingRead, () => content.IsDisposed);
        var lease = new StorageRuntimeDriverLease(driver);
        var pending = new TaskCompletionSource<ArtifactStorageReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = lease.Track(pending.Task);

        var disposal = lease.DisposeAsync();
        lease.Abandon(pending.Task);
        pending.SetResult(ArtifactStorageReadResult.Opened(content, 1, 1, new ArtifactStorageObjectMetadata { ObjectKey = "late", Length = 1 }));

        disposal.IsCompletedSuccessfully.ShouldBeTrue();
        await driver.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        content.IsDisposed.ShouldBeTrue();
        driver.CleanupWasCompleteAtDispose.ShouldBeTrue();
        driver.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Unified_lease_does_not_retain_successfully_released_streaming_operations()
    {
        const int operationCount = 8_192;
        var driver = new CapabilityDriver(StorageProviderCapabilities.StreamingRead);
        var lease = new StorageRuntimeDriverLease(driver);

        for (var index = 0; index < operationCount; index++)
        {
            var operation = Task.FromResult(index);
            _ = lease.Track(operation);
            (await operation).ShouldBe(index);
            lease.Release(operation);
        }

        lease.TrackedOperationCount.ShouldBe(0, "streaming memory must remain bounded by in-flight work rather than total artifact chunks");
        await lease.DisposeAsync();
        driver.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Verifying_stream_disposal_waits_for_an_inflight_provider_read_and_cleans_resources_exactly_once()
    {
        var content = new BlockingReadStream(42);
        var driver = new CapabilityDriver(StorageProviderCapabilities.StreamingRead, () => content.ReadCompleted && content.DisposeCalls == 1);
        var lease = new StorageRuntimeDriverLease(driver);
        var stream = new ArtifactCasVerifyingReadStream(content, lease, 1, SHA256.HashData([42]));
        var buffer = new byte[1];

        var read = stream.ReadAsync(buffer).AsTask();
        await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstDispose = stream.DisposeAsync().AsTask();
        var secondDispose = stream.DisposeAsync().AsTask();

        firstDispose.IsCompleted.ShouldBeFalse();
        driver.DisposeCalls.ShouldBe(0);
        content.DisposeCalls.ShouldBe(0);
        content.ReleaseRead.TrySetResult();

        (await read).ShouldBe(1);
        buffer[0].ShouldBe((byte)42);
        await Task.WhenAll(firstDispose, secondDispose);
        content.DisposeCalls.ShouldBe(1);
        driver.DisposeCalls.ShouldBe(1);
        driver.CleanupWasCompleteAtDispose.ShouldBeTrue();
    }

    [Fact]
    public async Task Verifying_stream_rejects_an_exact_length_corrupt_read_without_requiring_a_followup_eof_read()
    {
        var driver = new CapabilityDriver(StorageProviderCapabilities.StreamingRead);
        var lease = new StorageRuntimeDriverLease(driver);
        await using var stream = new ArtifactCasVerifyingReadStream(new MemoryStream([42]), lease, 1, SHA256.HashData([41]));

        var exception = await Should.ThrowAsync<InvalidDataException>(stream.ReadAsync(new byte[1]).AsTask());

        exception.Message.ShouldContain("SHA-256/size identity");
    }

    [Fact]
    public async Task Lease_and_verifying_stream_publish_shared_disposal_before_invoking_owned_cleanup()
    {
        var driver = new ReentrantDisposalDriver();
        var lease = new StorageRuntimeDriverLease(driver);
        driver.Lease = lease;
        var content = new ReentrantDisposalStream();
        var stream = new ArtifactCasVerifyingReadStream(content, lease, 0, SHA256.HashData([]));
        content.Owner = stream;

        await stream.DisposeAsync();

        content.ReentryBlocked.ShouldBeFalse();
        driver.ReentryBlocked.ShouldBeFalse();
        content.DisposeCalls.ShouldBe(1);
        driver.DisposeCalls.ShouldBe(1);
    }

    private static ArtifactCasProblem Problem(ArtifactCasProblemCode code, bool retryable = false) => new(code, retryable);

    private sealed class CapabilityDriver(StorageProviderCapabilities capabilities, Func<bool>? cleanupComplete = null) : IArtifactStorageDriver
    {
        public StorageProviderCapabilities Capabilities { get; } = capabilities;
        public int DisposeCalls { get; private set; }
        public bool CleanupWasCompleteAtDispose { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { CleanupWasCompleteAtDispose = cleanupComplete?.Invoke() ?? true; DisposeCalls++; Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class DisposalTrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            await base.DisposeAsync();
        }
    }

    private sealed class BlockingReadStream(byte value) : Stream
    {
        private int _disposeCalls;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadCompleted { get; private set; }
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await ReleaseRead.Task.WaitAsync(cancellationToken);
            buffer.Span[0] = value;
            ReadCompleted = true;
            return 1;
        }

        public override ValueTask DisposeAsync() { Interlocked.Increment(ref _disposeCalls); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref _disposeCalls); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }

    private sealed class ReentrantDisposalDriver : IArtifactStorageDriver
    {
        private int _disposeCalls;
        public StorageRuntimeDriverLease? Lease { private get; set; }
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.StreamingRead;
        public bool ReentryBlocked { get; private set; }
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            var callback = Task.Run(() => _ = Lease!.ToString());
            ReentryBlocked = !callback.Wait(TimeSpan.FromSeconds(1));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReentrantDisposalStream : MemoryStream
    {
        private int _disposeCalls;
        public ArtifactCasVerifyingReadStream? Owner { private get; set; }
        public bool ReentryBlocked { get; private set; }
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            var callback = Task.Run(() => _ = Owner!.DisposeAsync());
            ReentryBlocked = !callback.Wait(TimeSpan.FromSeconds(1));
            return ValueTask.CompletedTask;
        }
    }
}
