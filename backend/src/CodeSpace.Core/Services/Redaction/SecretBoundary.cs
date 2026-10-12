namespace CodeSpace.Core.Services.Redaction;

/// <summary>
/// Where a text that may still continue can be cut for exact-match redaction. A writer that persists a growing text in
/// pieces — a stream flushed by size or by time, a reader's bounded pass over one long line — redacts each piece on its
/// own, and exact matching cannot recognise a secret split between two of them. It releases the settled prefix and
/// carries the rest into the next piece. Shared by every redactor that writes in pieces, whatever its marker.
/// </summary>
internal static class SecretBoundary
{
    /// <summary>The longest prefix of <paramref name="text"/> that no secret runs across and after which nothing could yet become one.</summary>
    public static int SettledLength(string text, IReadOnlyList<string> secrets)
    {
        var settled = text.Length - LongestOpenSecretStart(text, secrets);

        while (StartOfSecretAcross(text, settled, secrets) is { } start) settled = start;

        return settled;
    }

    /// <summary>The length of the longest end of <paramref name="text"/> that is the start, but not the whole, of a secret.</summary>
    private static int LongestOpenSecretStart(string text, IReadOnlyList<string> secrets)
    {
        var longest = 0;

        foreach (var secret in secrets)
        {
            for (var length = Math.Min(secret.Length - 1, text.Length); length > longest; length--)
            {
                if (!text.AsSpan(text.Length - length).SequenceEqual(secret.AsSpan(0, length))) continue;

                longest = length;
                break;
            }
        }

        return longest;
    }

    /// <summary>Where a whole secret that runs across <paramref name="cut"/> starts, or null when none does.</summary>
    private static int? StartOfSecretAcross(string text, int cut, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            for (var start = Math.Max(0, cut - secret.Length + 1); start < cut; start++)
                if (text.AsSpan(start).StartsWith(secret, StringComparison.Ordinal)) return start;
        }

        return null;
    }
}
