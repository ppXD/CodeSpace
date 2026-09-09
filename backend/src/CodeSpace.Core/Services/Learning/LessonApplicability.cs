namespace CodeSpace.Core.Services.Learning;

/// <summary>Normalization and provenance validation for open runtime applicability selectors.</summary>
public static class LessonApplicability
{
    public const int MaxSelectorsPerDimension = 20;
    public const int MaxSelectorLength = 120;

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? values) => (values ?? [])
        .Select(Normalize).Where(value => value is not null).Select(value => value!)
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();

    public static bool AppliesTo(LessonRuntimeContext runtime, IReadOnlyList<string> models, IReadOnlyList<string> harnesses, IReadOnlyList<string> requiredTools)
    {
        var model = Normalize(runtime.Model);
        var harness = Normalize(runtime.Harness);
        var tools = Normalize(runtime.Tools);
        var normalizedModels = Normalize(models);
        var normalizedHarnesses = Normalize(harnesses);
        var normalizedRequiredTools = Normalize(requiredTools);
        return (normalizedModels.Count == 0 || model is not null && normalizedModels.Contains(model, StringComparer.Ordinal))
            && (normalizedHarnesses.Count == 0 || harness is not null && normalizedHarnesses.Contains(harness, StringComparer.Ordinal))
            && (normalizedRequiredTools.Count == 0 || normalizedRequiredTools.All(tools.Contains));
    }

    internal static string? Validate(IReadOnlyList<string> selectors, IEnumerable<IReadOnlyList<string>> observedByRun, string dimension)
    {
        if (selectors.Count > MaxSelectorsPerDimension) return $"{dimension} has {selectors.Count} selectors; the cap is {MaxSelectorsPerDimension}";
        if (selectors.Any(selector => selector.Length > MaxSelectorLength)) return $"{dimension} contains a selector longer than {MaxSelectorLength} characters";

        var observed = observedByRun.Select(values => Normalize(values).ToHashSet(StringComparer.Ordinal)).ToList();
        return selectors.FirstOrDefault(selector => observed.Any(run => !run.Contains(selector))) is { } missing
            ? $"{dimension} selector '{missing}' was not observed on every cited run"
            : null;
    }
}
