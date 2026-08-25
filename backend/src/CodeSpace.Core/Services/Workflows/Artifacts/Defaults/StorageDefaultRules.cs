using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>Pure, deterministic admission rules for the deployment storage template, shared with its tests.</summary>
internal static partial class StorageDefaultRules
{
    internal const int MaxNamespaceRootLength = 512;

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DataClassTypeKeyPattern();

    public static string NormalizeDataClassTypeKey(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length > 128 || !DataClassTypeKeyPattern().IsMatch(normalized))
            throw new ArgumentException("DataClassTypeKey must be an open versioned key such as 'agent-run-log/v1' using lowercase letters, digits, dots, or hyphens.");
        return normalized;
    }

    /// <summary>
    /// A ROOT, never a finished namespace. It is kept opaque here because only the materializer — and the provider
    /// capability that lane must add — knows which config field a given provider assembles it into. What this rule
    /// CAN guarantee is that it is one bounded, printable, non-empty token that a per-team segment can be appended to.
    /// </summary>
    public static string NormalizeNamespaceRoot(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaxNamespaceRootLength || normalized.Any(char.IsControl))
            throw new ArgumentException($"NamespaceRoot must be 1-{MaxNamespaceRootLength} visible characters. It is a ROOT: the materializer appends a per-team segment to it, so it must never already name one team.");
        return normalized;
    }

    /// <summary>
    /// The template's config is PARTIAL by construction: the provider's namespace field is assembled per team at
    /// materialization and merged in there, so the schema's <c>required</c> list cannot be satisfied at author time
    /// and is deliberately not asserted here. Everything that CAN be checked now is: the value is an object, carries
    /// no secret-schema property, and every property it does carry matches the provider's declaration.
    /// </summary>
    public static void ValidatePartialConfig(JsonElement config, IStorageProviderModule module)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new ArgumentException("NonSecretConfig must be a JSON object.");

        StorageProviderJson.ValidateSchema(module.ConfigSchema, "ConfigSchema");
        StorageProviderJson.ValidateSchema(module.SecretSchema, "SecretSchema");
        StorageProfileRules.EnsureNoSecretProperties(config, module.SecretSchema);
        StorageProviderJson.Validate(config, WithoutRequired(module.ConfigSchema), "NonSecretConfig", "ConfigSchema");
    }

    public static string CanonicalJson(JsonElement value) => StorageProfileRules.CanonicalJson(value);

    /// <summary>
    /// Which adoption policies a given data class may declare, derived from the class's own declaration rather than
    /// from a remembered list of keys.
    ///
    /// <para>A class that declares <see cref="IRoutedDataClassLocalFallback"/> HAS a durable home outside the routing
    /// plane, and materializing it takes that home away for good: once its route is Active,
    /// <c>StorageRouteRules.EnsureTransition</c> refuses any transition back to Draft, Retired is terminal, and a
    /// route cannot be deleted. "Overridable" means the route can be repointed at another destination, NOT that it
    /// can be returned to local. Auto-adopting that would commit every new team irreversibly without anyone choosing
    /// it, so such a class must be <see cref="StorageDefaultAdoptionPolicy.Explicit"/>.</para>
    ///
    /// <para>A class with no local home — Agent Run log capture is the shipped example — is refusing writes until it
    /// is cut over, so cutting it over takes nothing away and may be automatic.</para>
    /// </summary>
    public static void EnsureAdoptionPolicyAllowed(IRoutedDataClass dataClass, StorageDefaultAdoptionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(dataClass);
        if (!Enum.IsDefined(policy)) throw new ArgumentException($"Storage default adoption policy '{policy}' is not supported.");
        if (policy != StorageDefaultAdoptionPolicy.Automatic || dataClass is not IRoutedDataClassLocalFallback) return;

        throw new ArgumentException($"Data class '{dataClass.TypeKey}' keeps a durable home outside the routing plane, so its deployment default must be adopted Explicitly. Materializing it makes that team permanently unable to return to local storage for this class: an Active route cannot go back to Draft, Retired is terminal, and a route cannot be deleted.");
    }

    /// <summary>
    /// The provider's ConfigSchema with its top-level <c>required</c> list removed. Nested schemas are untouched: a
    /// property the template DOES carry must still be complete, only the absence of the namespace field is tolerated.
    /// </summary>
    private static JsonElement WithoutRequired(JsonElement configSchema)
    {
        if (configSchema.ValueKind != JsonValueKind.Object || !configSchema.TryGetProperty("required", out _)) return configSchema;

        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in configSchema.EnumerateObject().Where(property => property.Name != "required"))
                property.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
