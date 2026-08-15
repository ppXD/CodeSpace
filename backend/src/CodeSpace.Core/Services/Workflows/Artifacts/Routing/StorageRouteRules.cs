using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

internal static partial class StorageRouteRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DataClassTypeKeyPattern();

    public static string NormalizeDataClassTypeKey(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length > 128 || !DataClassTypeKeyPattern().IsMatch(normalized))
            throw new ArgumentException("DataClassTypeKey must be an open versioned key such as 'agent-run-log/v1' using lowercase letters, digits, dots, or hyphens.");
        return normalized;
    }

    public static void EnsureProfileSelection(StorageProfileRevisionMode mode, int? pinnedRevision)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException($"Storage profile revision mode '{mode}' is not supported.");
        if (mode == StorageProfileRevisionMode.CurrentAtWrite && pinnedRevision != null)
            throw new ArgumentException("CurrentAtWrite cannot carry a pinned profile revision.");
        if (mode == StorageProfileRevisionMode.Pinned && pinnedRevision is not > 0)
            throw new ArgumentException("Pinned requires an exact positive profile revision.");
    }

    public static void EnsureRevisionAllowed(StorageRouteState state)
    {
        if (state == StorageRouteState.Retired) throw new ArgumentException("A retired storage route is terminal and cannot receive a new revision.");
    }

    public static void EnsureTransition(StorageRouteState current, StorageRouteState requested)
    {
        if (!Enum.IsDefined(requested)) throw new ArgumentException($"Storage route state '{requested}' is not supported.");
        if (current == requested) return;
        if (current == StorageRouteState.Retired) throw new ArgumentException("A retired storage route is terminal and cannot change state.");
        if (requested == StorageRouteState.Draft) throw new ArgumentException("A storage route cannot transition back to Draft.");
    }
}
