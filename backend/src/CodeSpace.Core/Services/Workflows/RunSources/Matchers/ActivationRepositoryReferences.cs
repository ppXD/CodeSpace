using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Every repository an activation config NAMES, across both shapes the matchers honour: the PR triggers'
/// <c>repositories[].repositoryId</c> (<see cref="PrTriggerMatcherFilter"/>) and the legacy / push top-level
/// <c>repositoryId</c> (<see cref="PushTriggerMatcherFilter"/>). Kept beside those filters because it reads their
/// schema — a new shape they learn has to be learned here too, or it names repositories nothing validates.
///
/// <para>Only values a matcher could ever match on count: a string that parses as a Guid. Anything else is a
/// value no event can equal, so it scopes the activation to nothing outside its own team either way.</para>
/// </summary>
internal static class ActivationRepositoryReferences
{
    public static IReadOnlyList<Guid> Read(JsonElement activationConfig)
    {
        if (activationConfig.ValueKind != JsonValueKind.Object) return [];

        return ReadTopLevel(activationConfig).Concat(ReadRepositoriesList(activationConfig)).Distinct().ToList();
    }

    private static IEnumerable<Guid> ReadTopLevel(JsonElement config) =>
        TryReadRepositoryId(config, out var repositoryId) ? [repositoryId] : [];

    private static IEnumerable<Guid> ReadRepositoriesList(JsonElement config)
    {
        if (!config.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array) return [];

        return repositories.EnumerateArray()
            .Select(entry => TryReadRepositoryId(entry, out var repositoryId) ? repositoryId : (Guid?)null)
            .OfType<Guid>()
            .ToList();
    }

    private static bool TryReadRepositoryId(JsonElement scope, out Guid repositoryId)
    {
        repositoryId = Guid.Empty;

        if (scope.ValueKind != JsonValueKind.Object) return false;
        if (!scope.TryGetProperty("repositoryId", out var value) || value.ValueKind != JsonValueKind.String) return false;

        return Guid.TryParse(value.GetString(), out repositoryId);
    }
}
