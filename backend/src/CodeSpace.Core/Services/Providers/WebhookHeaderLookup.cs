namespace CodeSpace.Core.Services.Providers;

/// <summary>
/// Case-insensitive header lookup. ASP.NET passes header names with provider-defined casing
/// (X-GitHub-Event vs x-github-event), so every webhook component needs this DRY'd up.
/// </summary>
internal static class WebhookHeaderLookup
{
    public static bool TryFind(IReadOnlyDictionary<string, string> headers, string name, out string value)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
