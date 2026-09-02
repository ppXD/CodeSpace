using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Pins the one thing only this seam can be held to: it takes OWNERSHIP of an activated credential.
///
/// <para>The broker suite exercises every factory outcome, but it hands the activator a handle it built itself and
/// can no longer see, so it cannot observe whether the plaintext secret was released. A handle that survives one
/// refused activation keeps a decrypted provider secret reachable in memory for as long as the request scope lives,
/// and the second caller of this seam - Settings testing configuration nobody has saved - activates a secret an
/// operator typed one keystroke ago.</para>
/// </summary>
public class StorageDriverActivatorTests
{
    private const string ProviderTypeKey = "test-provider/v1";

    public enum FactoryAnswer
    {
        Driver,
        RefusesConfiguration,
        Throws,
        ReturnsNull,
        CancelsItself,
    }

    [Theory]
    [InlineData(FactoryAnswer.Driver)]
    [InlineData(FactoryAnswer.RefusesConfiguration)]
    [InlineData(FactoryAnswer.Throws)]
    [InlineData(FactoryAnswer.ReturnsNull)]
    [InlineData(FactoryAnswer.CancelsItself)]
    public async Task An_activated_credential_is_released_whatever_the_factory_answered(FactoryAnswer answer)
    {
        var credential = Handle();

        var resolution = await Activator().ActivateAsync(Factory(answer), Snapshot(), credential, CancellationToken.None);

        resolution.ShouldNotBeNull();
        Released(credential).ShouldBeTrue($"a {answer} activation still has to release the plaintext secret it was handed");
        if (resolution is StorageRuntimeDriverResolution.Ready ready) await ready.Lease.DisposeAsync();
    }

    /// <summary>
    /// Cancellation arriving before the factory is the one path that returns without ever entering the try/finally
    /// that releases the handle, so it is asserted apart from the theory above.
    /// </summary>
    [Fact]
    public async Task Cancellation_before_activation_releases_the_credential_and_never_reaches_the_provider()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var credential = Handle();
        var factory = new StubFactory(FactoryAnswer.Driver);

        var resolution = await Activator().ActivateAsync(factory, Snapshot(), credential, cancelled.Token);

        resolution.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization));
        factory.CreateCalls.ShouldBe(0, "a token already cancelled must not reach a provider at all");
        Released(credential).ShouldBeTrue("the handle is owned from the moment it is passed, not from the moment the factory is called");
    }

    /// <summary>A provider that needs no secret is activated with no handle at all, and that is not a failure.</summary>
    [Fact]
    public async Task A_provider_with_no_secret_activates_without_a_credential()
    {
        var resolution = await Activator().ActivateAsync(Factory(FactoryAnswer.Driver), Snapshot(), null, CancellationToken.None);

        var ready = resolution.ShouldBeOfType<StorageRuntimeDriverResolution.Ready>();
        await ready.Lease.DisposeAsync();
    }

    private static StorageDriverActivator Activator() => new(NullLogger<StorageDriverActivator>.Instance);

    private static IArtifactStorageDriverFactory Factory(FactoryAnswer answer) => new StubFactory(answer);

    private static StorageCredentialHandle Handle() => new(JsonDocument.Parse("""{"accessKeyId":"id","accessKeySecret":"secret"}""").RootElement);

    private static bool Released(StorageCredentialHandle credential)
    {
        try
        {
            credential.UseSecret(secret => secret.ValueKind);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static StorageProfileSnapshot Snapshot() => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileRevision = 1,
        ProviderTypeKey = ProviderTypeKey,
        Configuration = JsonDocument.Parse("""{"root":"/tmp/x"}""").RootElement,
    };

    private sealed class StubFactory : IArtifactStorageDriverFactory
    {
        private readonly FactoryAnswer _answer;
        public StubFactory(FactoryAnswer answer) { _answer = answer; }

        public int CreateCalls { get; private set; }
        public string ProviderTypeKey => StorageDriverActivatorTests.ProviderTypeKey;

        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return _answer switch
            {
                FactoryAnswer.Driver => ValueTask.FromResult<IArtifactStorageDriver>(new StubDriver()),
                FactoryAnswer.RefusesConfiguration => ValueTask.FromException<IArtifactStorageDriver>(new ArgumentException("the 'region' field is required")),
                FactoryAnswer.Throws => ValueTask.FromException<IArtifactStorageDriver>(new InvalidOperationException("provider blew up")),
                FactoryAnswer.ReturnsNull => ValueTask.FromResult<IArtifactStorageDriver>(null!),
                _ => ValueTask.FromException<IArtifactStorageDriver>(new OperationCanceledException()),
            };
        }
    }

    private sealed class StubDriver : IArtifactStorageDriver
    {
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
