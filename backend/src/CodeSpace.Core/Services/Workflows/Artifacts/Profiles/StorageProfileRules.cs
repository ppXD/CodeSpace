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
        RejectSecretProperties(config, secretSchema, "$");
        StorageProviderJson.Validate(config, configSchema, "NonSecretConfig", "ConfigSchema");
    }

    public static string CanonicalJson(JsonElement value) => StorageProviderJson.Canonicalize(value, "NonSecretConfig");

    public static string NamespaceFingerprint(string providerTypeKey, JsonElement namespaceConfig)
    {
        var canonical = CanonicalJson(namespaceConfig);
        var input = Encoding.UTF8.GetBytes($"storage-namespace/v1\n{providerTypeKey}\n{canonical}");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

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
