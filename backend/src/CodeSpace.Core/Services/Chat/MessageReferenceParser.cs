using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Chat;

/// <summary>
/// One reference extracted from a message body. The parser is purely <b>syntactic</b> — it does
/// not resolve <see cref="RefId"/> to a live entity (that is the resolver layer's job, kept out
/// so the parser stays zero-hardcode). <see cref="RefType"/> is an open namespace; any lowercase
/// identifier is accepted without a known-types list, so a new <c>@</c>-kind never touches this.
/// </summary>
public sealed record ParsedReference(string RefType, string RefId, string? Label);

/// <summary>
/// Extracts the generic <c>&lt;reftype:refid|label&gt;</c> reference tokens a message body carries.
///
/// <para>Grammar (one regex, no per-type branching):
/// <list type="bullet">
///   <item><c>reftype</c> — a lowercase identifier <c>[a-z][a-z0-9_]*</c> (<c>user</c>,
///         <c>pull_request</c>, <c>workflow_run</c>, <c>code_location</c>, …);</item>
///   <item><c>refid</c> — any run that is not <c>|</c> or <c>&gt;</c>, so it freely carries the
///         colons of a code-location (<c>repo:sha:path:line</c>) and the <c>#</c> of a PR;</item>
///   <item><c>|label</c> — optional display text, anything up to the closing <c>&gt;</c>.</item>
/// </list>
/// Anything that does not match the shape (missing colon, empty id, uppercase type, unclosed
/// token) is left as plain text — the body always renders standalone whether or not it parses.</para>
/// </summary>
public static partial class MessageReferenceParser
{
    [GeneratedRegex(@"<(?<type>[a-z][a-z0-9_]*):(?<id>[^|>]+)(?:\|(?<label>[^>]*))?>", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// The distinct references a body makes, in first-seen order. Duplicates of the same ordinal
    /// (RefType, RefId) collapse to one — keeping the first label — because the reverse index
    /// wants exactly one row per target per message, not one per mention.
    /// </summary>
    public static IReadOnlyList<ParsedReference> Parse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return Array.Empty<ParsedReference>();

        var seen = new HashSet<(string Type, string Id)>();
        var result = new List<ParsedReference>();

        foreach (Match match in TokenPattern().Matches(body))
        {
            var refType = match.Groups["type"].Value;
            var refId = match.Groups["id"].Value;

            if (!seen.Add((refType, refId))) continue;

            var labelGroup = match.Groups["label"];
            var label = labelGroup.Success && labelGroup.Value.Length > 0 ? labelGroup.Value : null;

            result.Add(new ParsedReference(refType, refId, label));
        }

        return result;
    }

    /// <summary>
    /// Renders a body to plain text for previews / notifications: each reference token collapses
    /// to its label (or its refId when unlabelled), so a list row reads "Hello @Alice" rather than
    /// "Hello &lt;user:…|Alice&gt;". A <c>user</c> mention keeps its leading <c>@</c> — the one
    /// universal mention sigil, mirroring the chip the UI shows; other ref types render bare.
    /// Surrounding text is untouched. Same grammar as <see cref="Parse"/>.
    /// </summary>
    public static string ToPlainText(string? body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;

        return TokenPattern().Replace(body, match =>
        {
            var label = match.Groups["label"];
            var text = label.Success && label.Value.Length > 0 ? label.Value : match.Groups["id"].Value;

            return match.Groups["type"].Value == "user" ? "@" + text : text;
        });
    }
}
