namespace CodeSpace.Core.Services.Redaction;

/// <summary>
/// A URL that may carry credentials — a model gateway's base URL with <c>user:key@host</c> or <c>?api-key=</c> — seen two
/// ways: as a DISPLAY form safe to show any reader (the credential slots masked, everything else verbatim), and as the
/// ENDPOINT a stored key is bound to (scheme, host and port: where the key is sent).
///
/// <para>The credential slots are the ones <see cref="CredentialNeedles.FromUrl"/> strikes from echoed text: the whole
/// userinfo, and each query value whose name <see cref="CredentialNeedles.MarksASecret">marks it a secret</see>. The
/// display form is computed on the raw string rather than through <see cref="Uri"/>, so a URL with nothing to mask reads
/// back exactly as it was stored — no host lower-casing, no trailing slash — and a URL <see cref="Uri"/> cannot parse
/// still has its userinfo masked.</para>
/// </summary>
public static class CredentialUrl
{
    /// <summary>What a credential slot reads as in the display form.</summary>
    public const string Mask = "***";

    /// <summary>The display form: userinfo and secret-named query values replaced by <see cref="Mask"/>; null/blank and a URL with no credential slot verbatim.</summary>
    public static string? Masked(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        var masked = url;

        foreach (var slot in CredentialSlots(url).OrderByDescending(slot => slot.Start)) masked = masked[..slot.Start] + MaskFor(url.Substring(slot.Start, slot.Length)) + masked[(slot.Start + slot.Length)..];

        return masked;
    }

    /// <summary>Whether a credential slot of <paramref name="url"/> still holds the <see cref="Mask"/> — a display form sent back with something else changed, which would store the placeholder as the credential.</summary>
    public static bool CarriesMask(string? url) => !string.IsNullOrEmpty(url) && CredentialSlots(url).Any(slot => url.AsSpan(slot.Start, slot.Length).Contains(Mask.AsSpan(), StringComparison.Ordinal));

    /// <summary>
    /// The URL an update stores for a field the read side shows <see cref="Masked">masked</see>: the display form sent
    /// back exactly as it was shown means "unchanged", so the stored URL — with its real embedded credentials — is kept,
    /// and an edit form or API client that PUTs back what it GETs keeps working. A <see cref="Mask"/> left in a credential
    /// slot of any OTHER submitted URL would store the placeholder as the credential itself, so it is refused: the caller
    /// re-types the credentials or removes them. Anything else, null included, is taken as submitted.
    /// </summary>
    public static string? KeepStoredIfMasked(string? stored, string? submitted)
    {
        if (submitted is not null && submitted == Masked(stored)) return stored;

        if (CarriesMask(submitted))
            throw new ArgumentException($"The URL still carries the masked placeholder ({Mask}) where a credential belongs. Send it back unchanged to keep the stored URL, or enter the embedded credentials in full (or remove them).");

        return submitted;
    }

    /// <summary>
    /// Whether two base URLs name the same endpoint — the same scheme, host and port, which is where a key presented to
    /// either one is sent. Path, query and userinfo may differ. Blank on both sides is the provider default on both sides.
    /// A value that is not an absolute http(s) URL only matches itself.
    /// </summary>
    public static bool SameEndpoint(string? stored, string? next)
    {
        if (string.IsNullOrWhiteSpace(stored) && string.IsNullOrWhiteSpace(next)) return true;

        if (HttpUri(stored) is not { } a || HttpUri(next) is not { } b) return string.Equals(stored?.Trim(), next?.Trim(), StringComparison.Ordinal);

        return Uri.Compare(a, b, UriComponents.Scheme | UriComponents.Host | UriComponents.StrongPort, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>The endpoint alone — <c>scheme://host:port</c>, never userinfo, path or query — for a log line about where a key is sent.</summary>
    public static string Endpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(provider default)";

        return HttpUri(url) is { } parsed ? $"{parsed.Scheme}://{parsed.IdnHost}:{parsed.Port}" : "(unparseable)";
    }

    private static Uri? HttpUri(string? url) => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) ? parsed : null;

    /// <summary>A userinfo slot keeps its <c>user:password</c> shape; a query value is one opaque token.</summary>
    private static string MaskFor(string slot) => slot.Contains(':') ? $"{Mask}:{Mask}" : Mask;

    /// <summary>The (start, length) of every credential slot in the raw string: the userinfo, then each non-empty secret-named query value.</summary>
    private static IEnumerable<(int Start, int Length)> CredentialSlots(string url)
    {
        var (authorityStart, authorityEnd) = AuthorityBounds(url);
        var at = url.AsSpan(authorityStart, authorityEnd - authorityStart).LastIndexOf('@');

        if (at > 0) yield return (authorityStart, at);

        foreach (var slot in SecretQueryValues(url, authorityEnd)) yield return slot;
    }

    /// <summary>Where the authority (<c>userinfo@host:port</c>) sits: after <c>scheme://</c> (or at the start when there is none), up to the first <c>/</c>, <c>?</c> or <c>#</c>.</summary>
    private static (int Start, int End) AuthorityBounds(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var start = scheme < 0 ? 0 : scheme + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);

        return (start, end < 0 ? url.Length : end);
    }

    private static IEnumerable<(int Start, int Length)> SecretQueryValues(string url, int authorityEnd)
    {
        var query = url.IndexOf('?', authorityEnd);
        if (query < 0) yield break;

        var fragment = url.IndexOf('#', query);
        var end = fragment < 0 ? url.Length : fragment;

        for (var pairStart = query + 1; pairStart < end;)
        {
            var pairEnd = url.IndexOf('&', pairStart, end - pairStart) is var amp and >= 0 ? amp : end;
            var equals = url.IndexOf('=', pairStart, pairEnd - pairStart);

            if (equals > pairStart && equals + 1 < pairEnd && CredentialNeedles.MarksASecret(Uri.UnescapeDataString(url[pairStart..equals]))) yield return (equals + 1, pairEnd - equals - 1);

            pairStart = pairEnd + 1;
        }
    }
}
