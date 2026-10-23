using CodeSpace.Core.Services.Slugs;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// The only slug derivation entry point delegates to <see cref="Slug.Slugify"/>, whose output is pinned by
/// golden-vector tests. If the result is empty (for example, all-Chinese or symbol-only input), callers must report
/// an error instead of silently deriving a fallback from an id; this slug becomes a permanent string in third-party configuration.
/// </summary>
public static class ExternalTriggerSlug
{
    public const string TypeKey = "trigger.external";

    public static string Derive(string? authorInput) => Slug.Slugify(authorInput);

    public static bool IsUsable(string? derived) => !string.IsNullOrWhiteSpace(derived);
}
