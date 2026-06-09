namespace CodeSpace.Core.Services.Providers.Source;

/// <summary>
/// Pure text helpers for commit summaries — the first-line extraction every provider applies to a raw
/// commit message before it hits the Code tab's single-line columns. SDK-free, unit-tested.
/// </summary>
public static class CommitSummaryText
{
    /// <summary>First line of a commit message, trimmed. Null/empty ⇒ "". CRLF and LF both terminate the line.</summary>
    public static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        var idx = message.IndexOfAny(new[] { '\n', '\r' });
        return (idx >= 0 ? message[..idx] : message).Trim();
    }

    /// <summary>Short 7-char SHA. Shorter inputs are returned as-is; null/empty ⇒ "".</summary>
    public static string ShortSha(string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return string.Empty;
        return sha.Length >= 7 ? sha[..7] : sha;
    }
}
