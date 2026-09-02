using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed class StorageConfigurationProbeService : IStorageConfigurationProbeService
{
    private readonly IStorageProviderModuleCatalog _modules;
    private readonly IArtifactStorageDriverFactoryCatalog _factories;
    private readonly IStorageDriverActivator _activator;

    public StorageConfigurationProbeService(IStorageProviderModuleCatalog modules, IArtifactStorageDriverFactoryCatalog factories, IStorageDriverActivator activator)
    {
        _modules = modules;
        _factories = factories;
        _activator = activator;
    }

    public async Task<StorageConfigurationProbeResult> ProbeAsync(StorageConfigurationProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();

        var admitted = Admit(request);
        if (admitted.Verdict != null) return Result(request.ProviderTypeKey, stopwatch, admitted.Verdict.Value);

        var factory = ResolveFactory(admitted.Module!.TypeKey);
        if (factory == null) return Result(request.ProviderTypeKey, stopwatch, StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderFactoryMissing, false));

        var resolution = await _activator.ActivateAsync(factory, Snapshot(admitted), Credential(admitted), cancellationToken).ConfigureAwait(false);
        if (resolution is not StorageRuntimeDriverResolution.Ready ready) return Result(request.ProviderTypeKey, stopwatch, StorageProbeVerdict.FromResolution(resolution));

        var observed = await StorageProbeRun.ExecuteAsync(ready.Lease, verifyWriteAccess: true, initialize: true, cancellationToken).ConfigureAwait(false);

        return Result(request.ProviderTypeKey, stopwatch, observed);
    }

    /// <summary>
    /// Runs the configuration and the secret through the SAME admission the save path runs them through - the
    /// module's own schemas, the no-secrets-in-configuration rule, and the provider's own readability check - and
    /// answers in the probe's closed vocabulary instead of throwing.
    ///
    /// <para>Sharing the gates is the point: a value this refuses is a value Settings would refuse, and a value it
    /// admits is one Settings will store byte-identically, because the canonical form asked about here is the form
    /// that would be persisted. Anything else and an operator could pass a test and then fail to save.</para>
    /// </summary>
    private Admission Admit(StorageConfigurationProbeRequest request)
    {
        IStorageProviderModule module;
        try
        {
            module = _modules.Require(request.ProviderTypeKey);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Admission.Refused(StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderModuleMissing, false));
        }

        JsonElement canonicalConfig;
        try
        {
            StorageProfileRules.ValidateConfig(request.NonSecretConfig, module.ConfigSchema, module.SecretSchema);
            canonicalConfig = JsonDocument.Parse(StorageProfileRules.CanonicalJson(request.NonSecretConfig)).RootElement.Clone();
            module.EnsureConfigurationReadable(canonicalConfig);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Admission.Refused(StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationInvalid, false));
        }

        return AdmitSecret(module, canonicalConfig, request.Secret);
    }

    private static Admission AdmitSecret(IStorageProviderModule module, JsonElement canonicalConfig, JsonElement? secret)
    {
        if (secret == null || secret.Value.ValueKind == JsonValueKind.Null)
            return RequiresSecret(module)
                ? Admission.Refused(StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialMissing, false))
                : new Admission(module, canonicalConfig, null, null);

        if (secret.Value.ValueKind != JsonValueKind.Object)
            return Admission.Refused(StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialSecretInvalid, false));

        try
        {
            StorageProviderJson.Validate(secret.Value, module.SecretSchema, "Secret");
            return new Admission(module, canonicalConfig, JsonDocument.Parse(StorageProviderJson.Canonicalize(secret.Value, "Secret")).RootElement.Clone(), null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Admission.Refused(StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialSecretInvalid, false));
        }
    }

    /// <summary>Whether this provider has no anonymous path at all, read off its own secret schema exactly as Settings reads it.</summary>
    private static bool RequiresSecret(IStorageProviderModule module) =>
        module.SecretSchema.TryGetProperty("required", out var required)
        && required.ValueKind == JsonValueKind.Array
        && required.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String);

    private IArtifactStorageDriverFactory? ResolveFactory(string providerTypeKey)
    {
        var factory = _factories.Get(providerTypeKey);

        return factory != null && string.Equals(factory.ProviderTypeKey, providerTypeKey, StringComparison.Ordinal) ? factory : null;
    }

    /// <summary>
    /// The snapshot a factory is handed for a destination that has no identity yet.
    ///
    /// <para>The revision is 1 and the id is freshly minted for this call alone: every factory refuses a snapshot
    /// with an empty id or a non-positive revision, because in the runtime an unidentified snapshot means a driver
    /// opened against something the plane cannot stamp a stored object with. Nothing is stamped here - the id is
    /// discarded when this method's caller returns, is never written anywhere, and no provider derives an address
    /// from it (both installed providers address purely from configuration).</para>
    /// </summary>
    private static StorageProfileSnapshot Snapshot(Admission admitted) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileRevision = 1,
        ProviderTypeKey = admitted.Module!.TypeKey,
        Configuration = admitted.CanonicalConfig,
    };

    /// <summary>
    /// Wraps the admitted secret in the same activation handle the runtime broker builds from the credential store,
    /// so a factory cannot tell the two apart - and so this secret is released on every path, the activator owning
    /// its disposal.
    /// </summary>
    private static StorageCredentialHandle? Credential(Admission admitted) =>
        admitted.CanonicalSecret == null ? null : new StorageCredentialHandle(admitted.CanonicalSecret.Value);

    private static StorageConfigurationProbeResult Result(string providerTypeKey, Stopwatch stopwatch, StorageProbeVerdict verdict) => new()
    {
        ProviderTypeKey = providerTypeKey,
        Status = verdict.Status,
        LatencyMilliseconds = Math.Max(0, stopwatch.ElapsedMilliseconds),
        Failure = verdict.Failure,
    };

    private sealed record Admission(IStorageProviderModule? Module, JsonElement CanonicalConfig, JsonElement? CanonicalSecret, StorageProbeVerdict? Verdict)
    {
        public static Admission Refused(StorageProbeVerdict verdict) => new(null, default, null, verdict);
    }
}
