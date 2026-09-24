using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Providers.Markdown;

/// <summary>
/// The tag appended to the free-text body of something a provider CREATES — a comment, an issue, a review — so
/// a retry can find what an earlier attempt of the same call already made instead of making it again. It is an
/// HTML comment: GitHub and GitLab drop it from the rendered markdown but keep it in the raw body their APIs
/// return. One marker per call, shared by every attempt of that call; the GUID means no other call's body can
/// carry it, so two concurrent creates with the same text never adopt each other's effect.
/// </summary>
internal static class IdempotencyMarker
{
    private static readonly Regex TrailingMarker = new(@"(?:\r?\n\r?\n)?<!-- codespace:idempotency:[0-9a-f]{32} -->[ \t\r\n]*\z", RegexOptions.Compiled);

    public static string New() => $"<!-- codespace:idempotency:{Guid.NewGuid():N} -->";

    /// <summary>The body to send: the caller's text, verbatim, then the marker on its own paragraph.</summary>
    public static string Append(string? body, string marker) => string.IsNullOrEmpty(body) ? marker : $"{body}\n\n{marker}";

    public static bool IsIn(string? body, string marker) => body != null && body.Contains(marker, StringComparison.Ordinal);

    /// <summary>
    /// The body as its author wrote it, for anything that SHOWS a body: CodeSpace's own pages render raw HTML as
    /// text, so the tag would print. Textual and anchored at the end — exactly one trailing marker goes, with the
    /// line breaks <see cref="Append"/> put before it (CRLF from a later web edit included), whatever markdown
    /// precedes it, an unclosed code fence too. A marker anywhere else — quoted in a code block, or followed by
    /// more text — is someone's text and stays. A body that was only the marker comes back empty.
    /// </summary>
    [return: NotNullIfNotNull(nameof(body))]
    public static string? Strip(string? body) => body == null ? null : TrailingMarker.Replace(body, string.Empty, 1);
}
