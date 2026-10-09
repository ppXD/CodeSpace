using System.Text.Json;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// A refused delivery's headers as its audit row keeps them: names always, values only for a few safe ones, and all of it
/// bounded. The row is written before anything is authenticated, so whoever posts chooses the header names and the
/// safe-listed values — sixty forged posts with a two-thousand-character header name once stored 135 KB. The provider's
/// own headers are kept first, so the cap never drops the ones an operator matches against the provider's delivery log.
/// </summary>
internal static class RejectedDeliveryHeaders
{
    public const int MaxHeaders = 40;

    public const int MaxNameLength = 64;

    public const int MaxValueLength = 256;

    /// <summary>The entry that counts what the cap left out.</summary>
    public const string OmittedKey = "(omitted)";

    private const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> SafeNames = new(StringComparer.OrdinalIgnoreCase) { "Content-Type", "User-Agent", "X-GitHub-Event", "X-GitHub-Delivery", "X-Gitlab-Event", "X-Gitlab-Event-UUID" };

    public static string Serialize(IReadOnlyDictionary<string, string> headers)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers.OrderByDescending(h => SafeNames.Contains(h.Key)).Take(MaxHeaders))
            kept[Truncate(name, MaxNameLength)] = SafeNames.Contains(name) ? Truncate(value, MaxValueLength) : Redacted;

        if (headers.Count > MaxHeaders) kept[OmittedKey] = $"{headers.Count - MaxHeaders} more headers not kept";

        return JsonSerializer.Serialize(kept);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : $"{text[..max]}…";
}
