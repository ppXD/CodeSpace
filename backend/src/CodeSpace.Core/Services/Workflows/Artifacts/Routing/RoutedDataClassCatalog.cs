using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// Collects every <see cref="IRoutedDataClass"/> the container discovered. Construction validates the declarations
/// themselves — a malformed or duplicated key is a build mistake, not an operator input — and never touches routing,
/// profile or credential state.
/// </summary>
public sealed class RoutedDataClassCatalog : IRoutedDataClassCatalog
{
    private static readonly Regex TypeKeyPattern = new("^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, IRoutedDataClass> _byTypeKey;

    public RoutedDataClassCatalog(IEnumerable<IRoutedDataClass> dataClasses)
    {
        ArgumentNullException.ThrowIfNull(dataClasses);

        var list = dataClasses.OrderBy(c => c?.TypeKey, StringComparer.Ordinal).ThenBy(c => c?.GetType().FullName, StringComparer.Ordinal).ToList();
        var byTypeKey = new Dictionary<string, IRoutedDataClass>(StringComparer.Ordinal);

        foreach (var dataClass in list)
        {
            Validate(dataClass);

            if (byTypeKey.TryAdd(dataClass.TypeKey, dataClass)) continue;

            var incumbent = byTypeKey[dataClass.TypeKey];
            throw new InvalidOperationException($"Routed data class '{dataClass.TypeKey}' is declared by both '{incumbent.GetType().FullName}' and '{dataClass.GetType().FullName}'. Every data class must have exactly one declaration.");
        }

        DataClasses = list.AsReadOnly();
        _byTypeKey = byTypeKey;
    }

    public IReadOnlyList<IRoutedDataClass> DataClasses { get; }

    public IRoutedDataClass? Get(string typeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        return _byTypeKey.TryGetValue(typeKey, out var dataClass) ? dataClass : null;
    }

    private static void Validate(IRoutedDataClass? dataClass)
    {
        if (dataClass == null) throw new InvalidOperationException("The routed data class catalog cannot contain a null declaration.");
        if (!TypeKeyPattern.IsMatch(dataClass.TypeKey ?? string.Empty))
            throw new InvalidOperationException($"Routed data class '{dataClass.GetType().FullName}' has invalid TypeKey '{dataClass.TypeKey}'. Use a canonical open versioned key such as 'workflow-artifact/v1'.");
        if (string.IsNullOrWhiteSpace(dataClass.DisplayName))
            throw new InvalidOperationException($"Routed data class '{dataClass.TypeKey}' has an empty DisplayName.");
    }
}
