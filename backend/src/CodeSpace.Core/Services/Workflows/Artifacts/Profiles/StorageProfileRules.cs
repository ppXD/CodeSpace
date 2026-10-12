using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>Pure, deterministic admission rules shared by the storage-profile control plane and its tests.</summary>
internal static class StorageProfileRules
{
    private static readonly Regex StableNamePattern = new("^[a-z0-9][a-z0-9-]{0,127}$", RegexOptions.CultureInvariant);

    public static string NormalizeStableName(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!StableNamePattern.IsMatch(normalized)) throw new ArgumentException("StableName must be 1-128 lowercase letters, digits, or hyphens and must start with a letter or digit.");
        return normalized;
    }

    public static void ValidateConfig(JsonElement config, JsonElement configSchema, JsonElement secretSchema)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new ArgumentException("NonSecretConfig must be a JSON object.");
        StorageProviderJson.ValidateSchema(configSchema, "ConfigSchema");
        StorageProviderJson.ValidateSchema(secretSchema, "SecretSchema");
        EnsureNoSecretProperties(config, secretSchema);
        StorageProviderJson.Validate(config, configSchema, "NonSecretConfig", "ConfigSchema");
    }

    /// <summary>
    /// The secret-leak half of <see cref="ValidateConfig"/>, on its own so a caller holding a config that is
    /// deliberately PARTIAL — the deployment-default template, whose namespace field is assembled per team later — can
    /// still run the one check that must never be skipped, without also asserting required-ness it cannot satisfy yet.
    /// </summary>
    public static void EnsureNoSecretProperties(JsonElement config, JsonElement secretSchema) => RejectSecretProperties(config, secretSchema, "$");

    public static string CanonicalJson(JsonElement value) => StorageProviderJson.Canonicalize(value, "NonSecretConfig");

    public static string NamespaceFingerprint(string providerTypeKey, JsonElement namespaceConfig)
    {
        var canonical = CanonicalJson(namespaceConfig);
        var input = Encoding.UTF8.GetBytes($"storage-namespace/v1\n{providerTypeKey}\n{canonical}");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    /// <summary>
    /// The identity of the PHYSICAL namespace a configuration reaches: the provider's own canonical projection of it,
    /// hashed. The one derivation behind the fingerprint a revision stores and behind the retention reaper's later
    /// comparison of two revisions' objects (<see cref="TryObjectIdentity"/>), so the two cannot disagree about what
    /// "the same namespace" means.
    /// </summary>
    public static string NamespaceFingerprint(IStorageProviderModule module, JsonElement nonSecretConfiguration)
    {
        var namespaceConfig = module.GetNamespaceConfiguration(nonSecretConfiguration);
        if (namespaceConfig.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Storage provider '{module.TypeKey}' namespace configuration projection must be a JSON object.");

        return NamespaceFingerprint(module.TypeKey, namespaceConfig);
    }

    /// <summary>
    /// WHICH physical object a STORED placement's key reaches, derived now from its revision's configuration rather
    /// than read from the fingerprint the revision stored, or null when it cannot be derived: the provider is not
    /// registered in this build, the configuration no longer parses, or the provider cannot place the key.
    ///
    /// <para>A provider that can name the object itself (<see cref="IStorageProviderPhysicalObjects"/>) is asked for it,
    /// because for a filesystem a link beneath the root can carry a key out of the namespace its root names. Every other
    /// provider's object is its namespace and the key together.</para>
    ///
    /// <para>Null is "unknown", never "different". A caller comparing objects must treat it as possibly the same object
    /// as anything at that key, which is why every failure lands here instead of propagating: the comparison this feeds
    /// decides whether bytes are deleted, and there only a kept byte is a safe mistake.</para>
    /// </summary>
    public static string? TryObjectIdentity(IStorageProviderModuleCatalog catalog, string providerTypeKey, string nonSecretConfigJson, string objectKey)
    {
        try
        {
            var module = catalog.Get(providerTypeKey);
            if (module == null) return null;

            using var configuration = JsonDocument.Parse(nonSecretConfigJson);
            return ObjectIdentity(module, configuration.RootElement, objectKey);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ObjectIdentity(IStorageProviderModule module, JsonElement configuration, string objectKey) =>
        module is IStorageProviderPhysicalObjects physical ? physical.PhysicalObject(configuration, objectKey) : $"{NamespaceFingerprint(module, configuration)}/{objectKey}";

    public static bool TryParseCredentialRef(string? value, out StorageProfileCredentialReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(':', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "db" || !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision <= 0)
            return false;

        reference = new StorageProfileCredentialReference(id, revision);
        return true;
    }

    /// <summary>
    /// Lifecycle state gates writes only. A read names one exact revision that durable bytes already stamped, so it
    /// stays admitted through Disabled and through terminal Retired; gating it here would make disabling a profile
    /// silently strand every artifact ever written under it. Blocking reads is a separate concept, not a side effect.
    /// </summary>
    public static bool Admits(StorageProfileState state, StorageProfileEligibility eligibility) =>
        eligibility == StorageProfileEligibility.Read || state == StorageProfileState.Active;

    public static void EnsureRevisionAllowed(StorageProfileState state)
    {
        if (state == StorageProfileState.Retired) throw new ArgumentException("A retired storage profile is terminal and cannot receive a new revision.");
    }

    public static void EnsureTransition(StorageProfileState current, StorageProfileState requested)
    {
        if (current == requested) return;
        if (current == StorageProfileState.Retired) throw new ArgumentException("A retired storage profile is terminal and cannot change state.");
        if (requested == StorageProfileState.Draft) throw new ArgumentException("A storage profile cannot transition back to Draft.");
        if (!Enum.IsDefined(requested)) throw new ArgumentException($"Storage profile state '{requested}' is not supported.");
    }

    private static void RejectSecretProperties(JsonElement config, JsonElement secretSchema, string path)
    {
        if (config.ValueKind != JsonValueKind.Object || secretSchema.ValueKind != JsonValueKind.Object) return;
        if (!secretSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return;

        foreach (var secret in properties.EnumerateObject())
        {
            if (!config.TryGetProperty(secret.Name, out var candidate)) continue;
            RejectSecretValue(candidate, secret.Value, Path(path, secret.Name));
        }
    }

    private static void RejectSecretValue(JsonElement candidate, JsonElement secretSchema, string path)
    {
        if (candidate.ValueKind == JsonValueKind.Object && secretSchema.ValueKind == JsonValueKind.Object
            && secretSchema.TryGetProperty("properties", out var nested) && nested.ValueKind == JsonValueKind.Object && nested.EnumerateObject().Any())
        {
            RejectSecretProperties(candidate, secretSchema, path);
            return;
        }

        if (candidate.ValueKind == JsonValueKind.Array && secretSchema.ValueKind == JsonValueKind.Object
            && secretSchema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in candidate.EnumerateArray()) RejectSecretValue(item, items, $"{path}[{index++}]");
            return;
        }

        throw new ArgumentException($"NonSecretConfig property '{path}' is a secret input and must be stored in a StorageCredential, never a profile revision.");
    }

    private static string Path(string parent, string property) => parent == "$" ? property : $"{parent}.{property}";
}

internal readonly record struct StorageProfileCredentialReference(Guid Id, int Revision)
{
    public string Canonical => $"db:{Id:D}:{Revision}";
}
