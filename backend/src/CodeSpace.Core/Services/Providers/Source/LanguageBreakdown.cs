using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Source;

/// <summary>
/// Pure normalization of a provider's language data into the Code tab's <see cref="RemoteLanguage"/> list:
/// GitHub reports bytes-per-language, GitLab reports percents directly — both funnel through here so the
/// ordering, capping, rounding, and zero-filtering are decided once and unit-tested without any SDK.
/// </summary>
public static class LanguageBreakdown
{
    /// <summary>The bar + legend don't need a long tail; cap the list so a polyglot repo stays legible.</summary>
    public const int MaxLanguages = 12;

    /// <summary>GitHub path: bytes per language → percent list (descending). Empty input ⇒ empty list.</summary>
    public static IReadOnlyList<RemoteLanguage> FromBytes(IReadOnlyDictionary<string, long> bytesByLanguage)
    {
        var total = bytesByLanguage.Values.Where(v => v > 0).Sum();
        if (total <= 0) return Array.Empty<RemoteLanguage>();

        return Normalize(bytesByLanguage.ToDictionary(kv => kv.Key, kv => kv.Value / (double)total * 100.0));
    }

    /// <summary>GitLab path: already-percent map → normalized list (descending). Empty input ⇒ empty list.</summary>
    public static IReadOnlyList<RemoteLanguage> FromPercents(IReadOnlyDictionary<string, double> percentByLanguage) =>
        Normalize(percentByLanguage);

    private static IReadOnlyList<RemoteLanguage> Normalize(IReadOnlyDictionary<string, double> percents) =>
        percents
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(MaxLanguages)
            .Select(kv => new RemoteLanguage { Name = kv.Key, Percent = Math.Round(kv.Value, 1) })
            .ToList();
}
