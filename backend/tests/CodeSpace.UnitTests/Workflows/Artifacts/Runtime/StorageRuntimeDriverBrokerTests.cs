using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

[Trait("Category", "Unit")]
public sealed class StorageRuntimeDriverBrokerTests
{
    private const string ProviderTypeKey = "test-object/v1";
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _profileId = Guid.NewGuid();

    [Fact]
    public void Contract_is_scoped_exact_and_keeps_runtime_handles_out_of_serializable_payloads()
    {
        typeof(IStorageRuntimeDriverBroker).GetInterfaces().ShouldContain(typeof(IScopedDependency));
        typeof(IStorageRuntimeDriverBroker).GetMethods().Select(method => method.Name).ShouldBe(["OpenAsync"]);
        typeof(IStorageRuntimeDriverBroker).GetMethod("OpenAsync")!.GetParameters().Length.ShouldBe(2);
        typeof(StorageRuntimeDriverRequest).GetProperties().Select(property => property.Name).ShouldBe(["TeamId", "ProfileId", "ProfileRevision", "Eligibility"]);
        typeof(StorageRuntimeDriverLease).GetInterfaces().ShouldContain(typeof(IAsyncDisposable));

        var runtimeProperties = typeof(StorageRuntimeDriverResolution).Assembly.GetTypes()
            .Where(type => type == typeof(StorageRuntimeDriverLease) || type == typeof(StorageCredentialHandle) || type == typeof(StorageRuntimeDriverResolution) || type.IsNested && type.DeclaringType == typeof(StorageRuntimeDriverResolution))
            .SelectMany(type => type.GetProperties())
            .ToList();
        runtimeProperties.ShouldNotContain(property => property.PropertyType == typeof(JsonElement) && property.GetMethod != null && property.GetMethod.IsPublic);
        runtimeProperties.ShouldNotContain(property => property.Name.Contains("Exception", StringComparison.Ordinal) || property.Name.Contains("Message", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing", StorageRuntimeProfileFailureReason.Missing)]
    [InlineData("inactive", StorageRuntimeProfileFailureReason.NotActive)]
    [InlineData("revision", StorageRuntimeProfileFailureReason.RevisionMissing)]
    [InlineData("resolver", StorageRuntimeProfileFailureReason.ResolutionFailed)]
    public async Task Profile_failures_are_typed_and_stop_before_credential_or_factory_activation(string scenario, StorageRuntimeProfileFailureReason expected)
    {
        var profile = new StubProfileResolver((_, _) => scenario switch
        {
            "missing" => Task.FromResult<StorageProfileSnapshotResolution>(new StorageProfileSnapshotResolution.Missing()),
            "inactive" => Task.FromResult<StorageProfileSnapshotResolution>(new StorageProfileSnapshotResolution.NotActive(StorageProfileState.Disabled)),
            "revision" => Task.FromResult<StorageProfileSnapshotResolution>(new StorageProfileSnapshotResolution.RevisionMissing()),
            _ => Task.FromException<StorageProfileSnapshotResolution>(new InvalidOperationException("database detail must not escape")),
        });
        var credential = new StubCredentialResolver();
        var factory = new StubFactory();

        var result = await Broker(profile, credential, factory).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBe(new StorageRuntimeDriverResolution.ProfileUnavailable(expected));
        credential.Calls.ShouldBe(0);
        factory.CreateCalls.ShouldBe(0);
        result.ToString().ShouldNotContain("database detail must not escape", Case.Sensitive);
    }

    [Fact]
    public async Task Broker_preserves_the_exact_team_profile_revision_and_rejects_a_forged_snapshot_before_factory_activation()
    {
        StorageProfileSnapshotRequest? observed = null;
        var profile = new StubProfileResolver((request, _) =>
        {
            observed = request;
            return Task.FromResult<StorageProfileSnapshotResolution>(new StorageProfileSnapshotResolution.Ready(Profile(profileId: Guid.NewGuid())));
        });
        var factory = new StubFactory();

        var result = await Broker(profile, new StubCredentialResolver(), factory).OpenAsync(Request(), CancellationToken.None);

        observed.ShouldBe(new StorageProfileSnapshotRequest(_teamId, _profileId, 7, StorageProfileEligibility.Write));
        result.ShouldBe(new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch));
        factory.CreateCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("schema")]
    [InlineData("configuration")]
    public async Task Snapshot_provider_schema_and_configuration_are_revalidated_before_catalog_or_factory_use(string scenario)
    {
        var snapshot = scenario switch
        {
            "provider" => Profile(providerTypeKey: ""),
            "schema" => Profile(schemaVersion: StorageProfileSnapshot.CurrentSchemaVersion + 1),
            _ => Profile(configuration: JsonSerializer.SerializeToElement(new[] { "not-an-object" })),
        };
        var catalog = new StubCatalog(new StubFactory());

        var result = await Broker(ProfileResolver(snapshot), new StubCredentialResolver(), catalog).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBeOfType<StorageRuntimeDriverResolution.ConfigurationInvalid>();
        catalog.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Credential_resolution_is_exact_ephemeral_redacted_and_destroyed_before_the_driver_lease_escapes()
    {
        var credentialId = Guid.NewGuid();
        var reference = new StorageSecretReference("database/v1", credentialId.ToString("D"), "17");
        StorageCredentialSecretRequest? observed = null;
        StorageCredentialSecretResolution.Ready? capturedSecret = null;
        var credential = new StubCredentialResolver((request, _) =>
        {
            observed = request;
            capturedSecret = new StorageCredentialSecretResolution.Ready(JsonSerializer.SerializeToElement(new { accessKey = "never-log-this" }));
            JsonSerializer.Serialize(capturedSecret).ShouldNotContain("never-log-this", Case.Sensitive);
            return Task.FromResult<StorageCredentialSecretResolution>(capturedSecret);
        });
        var driver = new StubDriver();
        StorageCredentialHandle? capturedHandle = null;
        var factory = new StubFactory((request, _) =>
        {
            capturedHandle = request.CredentialHandle.ShouldNotBeNull();
            capturedHandle.UseSecret(secret => secret.GetProperty("accessKey").GetString()).ShouldBe("never-log-this");
            capturedHandle.ToString().ShouldNotContain("never-log-this", Case.Sensitive);
            Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize(capturedHandle));
            var serializedRequest = JsonSerializer.Serialize(request);
            serializedRequest.ShouldNotContain("CredentialHandle", Case.Sensitive);
            serializedRequest.ShouldNotContain("never-log-this", Case.Sensitive);
            return ValueTask.FromResult<IArtifactStorageDriver>(driver);
        });

        var result = await Broker(ProfileResolver(Profile(secretReference: reference)), credential, factory).OpenAsync(Request(), CancellationToken.None);

        observed.ShouldBe(new StorageCredentialSecretRequest(_teamId, credentialId, 17, ProviderTypeKey));
        var ready = result.ShouldBeOfType<StorageRuntimeDriverResolution.Ready>();
        ready.Lease.Driver.ShouldBeSameAs(driver);
        Should.Throw<ObjectDisposedException>(() => capturedSecret!.UseSecret(secret => secret.ValueKind));
        Should.Throw<ObjectDisposedException>(() => capturedHandle!.UseSecret(secret => secret.ValueKind));
        Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize(ready.Lease));
        ready.ToString().ShouldNotContain("never-log-this", Case.Sensitive);

        await ready.Lease.DisposeAsync();
        driver.DisposeCalls.ShouldBe(1);
        await ready.Lease.DisposeAsync();
        driver.DisposeCalls.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => _ = ready.Lease.Driver);
    }

    [Theory]
    [InlineData("vault/v1", "11111111-2222-3333-4444-555555555555", "1")]
    [InlineData("database/v1", "00000000-0000-0000-0000-000000000000", "1")]
    [InlineData("database/v1", "11111111222233334444555555555555", "1")]
    [InlineData("database/v1", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", "1")]
    [InlineData("database/v1", "11111111-2222-3333-4444-555555555555", null)]
    [InlineData("database/v1", "11111111-2222-3333-4444-555555555555", "01")]
    [InlineData("database/v1", "11111111-2222-3333-4444-555555555555", "0")]
    public async Task Noncanonical_or_non_database_credential_references_never_fall_back_or_reach_secret_resolution(string storeType, string secretId, string? version)
    {
        var credential = new StubCredentialResolver();
        var factory = new StubFactory();
        var snapshot = Profile(secretReference: new StorageSecretReference(storeType, secretId, version));

        var result = await Broker(ProfileResolver(snapshot), credential, factory).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBe(new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidReference));
        credential.Calls.ShouldBe(0);
        factory.CreateCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("missing", StorageRuntimeCredentialFailureReason.Missing)]
    [InlineData("inactive", StorageRuntimeCredentialFailureReason.NotActive)]
    [InlineData("revision", StorageRuntimeCredentialFailureReason.RevisionMissing)]
    [InlineData("provider", StorageRuntimeCredentialFailureReason.ProviderMismatch)]
    [InlineData("module", StorageRuntimeCredentialFailureReason.ProviderUnavailable)]
    [InlineData("envelope", StorageRuntimeCredentialFailureReason.InvalidEnvelope)]
    [InlineData("resolver", StorageRuntimeCredentialFailureReason.ResolutionFailed)]
    public async Task Credential_failures_are_typed_and_never_activate_the_factory(string scenario, StorageRuntimeCredentialFailureReason expected)
    {
        var credential = new StubCredentialResolver((_, _) => scenario switch
        {
            "missing" => Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.Missing()),
            "inactive" => Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.NotActive(StorageCredentialState.Revoked)),
            "revision" => Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.RevisionMissing()),
            "provider" => Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.ProviderMismatch()),
            "module" => Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.ProviderUnavailable(StorageCredentialProviderUnavailableReason.ModuleMissing)),
            "envelope" => Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.Decryption)),
            _ => Task.FromException<StorageCredentialSecretResolution>(new InvalidOperationException("secret-bearing provider error")),
        });
        var factory = new StubFactory();
        var snapshot = Profile(secretReference: new StorageSecretReference("database/v1", Guid.NewGuid().ToString("D"), "2"));

        var result = await Broker(ProfileResolver(snapshot), credential, factory).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBe(new StorageRuntimeDriverResolution.CredentialUnavailable(expected));
        factory.CreateCalls.ShouldBe(0);
        result.ToString().ShouldNotContain("secret-bearing provider error", Case.Sensitive);
    }

    [Theory]
    [InlineData("missing", StorageRuntimeProviderFailureReason.FactoryMissing)]
    [InlineData("mismatch", StorageRuntimeProviderFailureReason.FactoryMismatch)]
    [InlineData("catalog", StorageRuntimeProviderFailureReason.CatalogFailure)]
    public async Task Provider_catalog_is_exact_and_fail_closed_before_secret_resolution(string scenario, StorageRuntimeProviderFailureReason expected)
    {
        var credential = new StubCredentialResolver();
        var catalog = scenario switch
        {
            "missing" => new StubCatalog((IArtifactStorageDriverFactory?)null),
            "mismatch" => new StubCatalog(new StubFactory("other-object/v1")),
            _ => new StubCatalog(new InvalidOperationException("catalog internals")),
        };
        var snapshot = Profile(secretReference: new StorageSecretReference("database/v1", Guid.NewGuid().ToString("D"), "2"));

        var result = await Broker(ProfileResolver(snapshot), credential, catalog).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBe(new StorageRuntimeDriverResolution.ProviderUnavailable(expected));
        catalog.Keys.ShouldBe([ProviderTypeKey]);
        credential.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("null", StorageRuntimeDriverInitializationFailureReason.NullDriver)]
    [InlineData("cancel", StorageRuntimeDriverInitializationFailureReason.ProviderCanceled)]
    [InlineData("exception", StorageRuntimeDriverInitializationFailureReason.ProviderFailure)]
    public async Task Driver_initialization_failures_are_typed_without_exception_or_secret_text(string scenario, StorageRuntimeDriverInitializationFailureReason expected)
    {
        var factory = new StubFactory((_, _) => scenario switch
        {
            "null" => ValueTask.FromResult<IArtifactStorageDriver>(null!),
            "cancel" => ValueTask.FromException<IArtifactStorageDriver>(new OperationCanceledException("provider timeout included a secret")),
            _ => ValueTask.FromException<IArtifactStorageDriver>(new InvalidOperationException("provider included a secret")),
        });

        var result = await Broker(ProfileResolver(Profile()), new StubCredentialResolver(), factory).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBe(new StorageRuntimeDriverResolution.DriverInitializationFailed(expected));
        result.ToString().ShouldNotContain("secret", Case.Insensitive);
    }

    [Fact]
    public async Task Factory_configuration_rejection_is_distinct_from_provider_initialization_failure()
    {
        var factory = new StubFactory((_, _) => ValueTask.FromException<IArtifactStorageDriver>(new ArgumentException("configuration accidentally included a secret")));

        var result = await Broker(ProfileResolver(Profile()), new StubCredentialResolver(), factory).OpenAsync(Request(), CancellationToken.None);

        result.ShouldBe(new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.FactoryRejectedConfiguration));
        result.ToString().ShouldNotContain("secret", Case.Insensitive);
    }

    [Fact]
    public async Task Supplied_cancellation_is_typed_at_each_async_boundary_and_never_leaks_a_created_driver()
    {
        using var beforeProfile = new CancellationTokenSource();
        beforeProfile.Cancel();
        var untouchedProfile = ProfileResolver(Profile());
        var beforeProfileResult = await Broker(untouchedProfile, new StubCredentialResolver(), new StubFactory()).OpenAsync(Request(), beforeProfile.Token);
        beforeProfileResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.ProfileResolution));
        untouchedProfile.Calls.ShouldBe(0);

        using var duringCredential = new CancellationTokenSource();
        var credential = new StubCredentialResolver((_, _) =>
        {
            duringCredential.Cancel();
            return Task.FromCanceled<StorageCredentialSecretResolution>(duringCredential.Token);
        });
        var credentialResult = await Broker(ProfileResolver(Profile(secretReference: new StorageSecretReference("database/v1", Guid.NewGuid().ToString("D"), "1"))), credential, new StubFactory())
            .OpenAsync(Request(), duringCredential.Token);
        credentialResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.CredentialResolution));

        using var readyCredentialCancellation = new CancellationTokenSource();
        var readySecret = new StorageCredentialSecretResolution.Ready(JsonSerializer.SerializeToElement(new { accessKey = "dispose-on-cancel" }));
        var readyCredential = new StubCredentialResolver((_, _) =>
        {
            readyCredentialCancellation.Cancel();
            return Task.FromResult<StorageCredentialSecretResolution>(readySecret);
        });
        var untouchedFactory = new StubFactory();
        var readyCredentialResult = await Broker(ProfileResolver(Profile(secretReference: new StorageSecretReference("database/v1", Guid.NewGuid().ToString("D"), "1"))), readyCredential, untouchedFactory)
            .OpenAsync(Request(), readyCredentialCancellation.Token);
        readyCredentialResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.CredentialResolution));
        Should.Throw<ObjectDisposedException>(() => readySecret.UseSecret(secret => secret.ValueKind));
        untouchedFactory.CreateCalls.ShouldBe(0);

        using var duringFactory = new CancellationTokenSource();
        var driver = new StubDriver();
        var factory = new StubFactory((_, _) =>
        {
            duringFactory.Cancel();
            return ValueTask.FromResult<IArtifactStorageDriver>(driver);
        });
        var factoryResult = await Broker(ProfileResolver(Profile()), new StubCredentialResolver(), factory).OpenAsync(Request(), duringFactory.Token);
        factoryResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization));
        driver.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Recoverable_faults_cannot_mask_supplied_cancellation_at_any_broker_stage()
    {
        using var profileCancellation = new CancellationTokenSource();
        var profile = new StubProfileResolver((_, _) =>
        {
            profileCancellation.Cancel();
            return Task.FromException<StorageProfileSnapshotResolution>(new IOException("translated profile cancellation"));
        });
        var profileResult = await Broker(profile, new StubCredentialResolver(), new StubFactory()).OpenAsync(Request(), profileCancellation.Token);
        profileResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.ProfileResolution));

        using var catalogCancellation = new CancellationTokenSource();
        var catalogResult = await Broker(ProfileResolver(Profile()), new StubCredentialResolver(), new CancellingCatalog(catalogCancellation))
            .OpenAsync(Request(), catalogCancellation.Token);
        catalogResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization));

        using var credentialCancellation = new CancellationTokenSource();
        var credential = new StubCredentialResolver((_, _) =>
        {
            credentialCancellation.Cancel();
            return Task.FromException<StorageCredentialSecretResolution>(new InvalidOperationException("translated credential cancellation"));
        });
        var credentialResult = await Broker(ProfileResolver(Profile(secretReference: new StorageSecretReference("database/v1", Guid.NewGuid().ToString("D"), "1"))), credential, new StubFactory())
            .OpenAsync(Request(), credentialCancellation.Token);
        credentialResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.CredentialResolution));

        using var factoryCancellation = new CancellationTokenSource();
        var factory = new StubFactory((_, _) =>
        {
            factoryCancellation.Cancel();
            return ValueTask.FromException<IArtifactStorageDriver>(new ArgumentException("translated factory cancellation"));
        });
        var factoryResult = await Broker(ProfileResolver(Profile()), new StubCredentialResolver(), factory).OpenAsync(Request(), factoryCancellation.Token);
        factoryResult.ShouldBe(new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization));
    }

    [Fact]
    public async Task Concurrent_lease_disposal_is_exactly_once_and_every_caller_awaits_the_same_driver_cleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new StubDriver(async () =>
        {
            started.SetResult();
            await release.Task;
        });
        var factory = new StubFactory((_, _) => ValueTask.FromResult<IArtifactStorageDriver>(driver));
        var result = await Broker(ProfileResolver(Profile()), new StubCredentialResolver(), factory).OpenAsync(Request(), CancellationToken.None);
        var lease = result.ShouldBeOfType<StorageRuntimeDriverResolution.Ready>().Lease;

        var first = lease.DisposeAsync().AsTask();
        await started.Task;
        var second = lease.DisposeAsync().AsTask();

        second.IsCompleted.ShouldBeFalse();
        driver.DisposeCalls.ShouldBe(1);
        release.SetResult();
        await Task.WhenAll(first, second);
        driver.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Profile_resolver_readiness_vocabulary_maps_to_the_broker_categories_without_calling_factory()
    {
        var cases = new (StorageProfileSnapshotResolution Resolution, StorageRuntimeDriverResolution Expected)[]
        {
            (new StorageProfileSnapshotResolution.ProviderUnavailable(ProviderTypeKey, StorageProfileProviderUnavailableReason.ModuleMissing), new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.ModuleMissing)),
            (new StorageProfileSnapshotResolution.ProviderUnavailable(ProviderTypeKey, StorageProfileProviderUnavailableReason.FactoryMissing), new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.FactoryMissing)),
            (new StorageProfileSnapshotResolution.Invalid(StorageProfileSnapshotInvalidReason.Configuration), new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.InvalidConfiguration)),
            (new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.Missing), new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.Missing)),
            (new StorageProfileSnapshotResolution.CredentialInvalid(StorageProfileCredentialInvalidReason.MalformedReference), new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidReference)),
        };

        foreach (var item in cases)
        {
            var factory = new StubFactory();
            var result = await Broker(new StubProfileResolver((_, _) => Task.FromResult(item.Resolution)), new StubCredentialResolver(), factory).OpenAsync(Request(), CancellationToken.None);
            result.ShouldBe(item.Expected);
            factory.CreateCalls.ShouldBe(0);
        }
    }

    private StorageRuntimeDriverBroker Broker(IStorageProfileSnapshotResolver profile, IStorageCredentialSecretResolver credential, IArtifactStorageDriverFactory factory) =>
        Broker(profile, credential, new StubCatalog(factory));

    private static StorageRuntimeDriverBroker Broker(IStorageProfileSnapshotResolver profile, IStorageCredentialSecretResolver credential, IArtifactStorageDriverFactoryCatalog catalog) =>
        new(profile, credential, catalog);

    private StorageRuntimeDriverRequest Request() => new(_teamId, _profileId, 7, StorageProfileEligibility.Write);

    private StubProfileResolver ProfileResolver(StorageProfileSnapshot snapshot) =>
        new((_, _) => Task.FromResult<StorageProfileSnapshotResolution>(new StorageProfileSnapshotResolution.Ready(snapshot)));

    private StorageProfileSnapshot Profile(Guid? profileId = null, string providerTypeKey = ProviderTypeKey, int schemaVersion = StorageProfileSnapshot.CurrentSchemaVersion, JsonElement? configuration = null, StorageSecretReference? secretReference = null) => new()
    {
        SchemaVersion = schemaVersion,
        ProfileId = profileId ?? _profileId,
        ProfileRevision = 7,
        ProviderTypeKey = providerTypeKey,
        Configuration = configuration ?? JsonSerializer.SerializeToElement(new { bucket = "test" }),
        SecretReference = secretReference,
    };

    private sealed class StubProfileResolver(Func<StorageProfileSnapshotRequest, CancellationToken, Task<StorageProfileSnapshotResolution>>? resolve = null) : IStorageProfileSnapshotResolver
    {
        public int Calls { get; private set; }

        public Task<StorageProfileSnapshotResolution> ResolveAsync(StorageProfileSnapshotRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return resolve?.Invoke(request, cancellationToken) ?? Task.FromResult<StorageProfileSnapshotResolution>(new StorageProfileSnapshotResolution.Missing());
        }
    }

    private sealed class StubCredentialResolver(Func<StorageCredentialSecretRequest, CancellationToken, Task<StorageCredentialSecretResolution>>? resolve = null) : IStorageCredentialSecretResolver
    {
        public int Calls { get; private set; }

        public Task<StorageCredentialSecretResolution> ResolveAsync(StorageCredentialSecretRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return resolve?.Invoke(request, cancellationToken) ?? Task.FromResult<StorageCredentialSecretResolution>(new StorageCredentialSecretResolution.Missing());
        }
    }

    private sealed class StubCatalog : IArtifactStorageDriverFactoryCatalog
    {
        private readonly IArtifactStorageDriverFactory? _factory;
        private readonly Exception? _error;

        public StubCatalog(IArtifactStorageDriverFactory? factory) => _factory = factory;
        public StubCatalog(Exception error) => _error = error;
        public int GetCalls { get; private set; }
        public List<string> Keys { get; } = [];

        public IArtifactStorageDriverFactory? Get(string providerTypeKey)
        {
            GetCalls++;
            Keys.Add(providerTypeKey);
            if (_error != null) throw _error;
            return _factory;
        }

        public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException();
    }

    private sealed class CancellingCatalog(CancellationTokenSource cancellation) : IArtifactStorageDriverFactoryCatalog
    {
        public IArtifactStorageDriverFactory? Get(string providerTypeKey)
        {
            cancellation.Cancel();
            throw new InvalidOperationException("translated catalog cancellation");
        }

        public IArtifactStorageDriverFactory Require(string providerTypeKey) => throw new InvalidOperationException();
    }

    private sealed class StubFactory : IArtifactStorageDriverFactory
    {
        private readonly Func<ArtifactStorageDriverCreateRequest, CancellationToken, ValueTask<IArtifactStorageDriver>>? _create;

        public StubFactory(string providerTypeKey = StorageRuntimeDriverBrokerTests.ProviderTypeKey) => ProviderTypeKey = providerTypeKey;
        public StubFactory(Func<ArtifactStorageDriverCreateRequest, CancellationToken, ValueTask<IArtifactStorageDriver>> create, string providerTypeKey = StorageRuntimeDriverBrokerTests.ProviderTypeKey)
        {
            _create = create;
            ProviderTypeKey = providerTypeKey;
        }

        public string ProviderTypeKey { get; }
        public int CreateCalls { get; private set; }

        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return _create?.Invoke(request, cancellationToken) ?? ValueTask.FromResult<IArtifactStorageDriver>(new StubDriver());
        }
    }

    private sealed class StubDriver(Func<ValueTask>? dispose = null) : IArtifactStorageDriver
    {
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public int DisposeCalls { get; private set; }
        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return dispose?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
