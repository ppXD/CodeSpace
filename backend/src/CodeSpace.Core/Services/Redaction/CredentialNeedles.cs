using System.Diagnostics.CodeAnalysis;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Redaction;

/// <summary>
/// The values a credential PRESENTS to an upstream — the exact strings an upstream that echoes its input (a 401 body
/// quoting the key it rejected, a proxy that reflects request headers) can hand back into text the platform persists or
/// shows. One rule, shared by every place that has to strike such an echo: the agent run's redactor, the in-process
/// model transport, and the git-provider error translation.
///
/// <para>Needles are exact values, never shapes. A shape guess (<c>sk-…</c>, <c>glpat-…</c>) is a fresh leak the moment
/// a gateway picks a shape it doesn't match; an exact value is struck wherever it appears, in whatever text.</para>
/// </summary>
public static class CredentialNeedles
{
    /// <summary>
    /// The shortest value usable as a redaction needle. Below this a value is a fragment, not an identifier —
    /// striking it would shred unrelated text (an injected <c>1</c> would hit every line carrying a digit), so it is
    /// left readable. The SAME threshold the real-model artifact redactor applies (<c>MIN_NEEDLE_LENGTH</c> in
    /// <c>.github/scripts/collect-real-model-verdicts.sh</c>); no real api key or access token is shorter.
    /// </summary>
    public const int MinimumLength = 8;

    /// <summary>
    /// Name fragments that mark a value a SECRET — applied to an injected environment variable's name, a URL query
    /// parameter's name and a request header's name alike. All three are opaque name/value string pairs with no
    /// per-entry secret flag to read, so the name is the only signal available — and it is the signal that separates
    /// the token from the values beside it that must stay READABLE: the gateway base URL, the model tier pins, an
    /// <c>api-version</c> stamp. Those are what an operator reads the error FOR, and masking a low-entropy one would
    /// shred every unrelated line that happens to carry it (an <c>api-version</c> date also matches every timestamp of
    /// that day).
    ///
    /// <para>Substring matching over a deliberately WIDE list: a marker earns its place by naming a carrier that
    /// authenticates something (<c>PASSPHRASE</c> on a signing key, <c>PWD</c> on <c>MYSQL_PWD</c>, <c>DSN</c> and
    /// <c>CONNECTION_STRING</c> on a database URL with its password inline, <c>COOKIE</c> on a session, <c>WEBHOOK</c>
    /// on a URL whose path IS the capability). The known cost is a false-positive class — <c>KEY_ID</c>,
    /// <c>TOKEN_LIMIT</c>, an injected <c>PWD</c> — whose values are masked though they are not secret. That is the
    /// side to err on: a masked path is a garbled line an operator can work around, an unmasked token is a leak that
    /// the append-only log can never take back.</para>
    /// </summary>
    private static readonly string[] SecretNameMarkers =
        ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "PWD", "CREDENTIAL", "AUTH", "COOKIE", "DSN", "CONNECTION_STRING", "CONNSTR", "PRIVATE", "WEBHOOK"];

    /// <summary>Whether a name — an env variable's, a URL query parameter's or a header's — marks its value a secret.</summary>
    public static bool MarksASecret(string name) => SecretNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The credential parts embedded IN a URL — every userinfo segment, and each query value whose parameter name
    /// <see cref="MarksASecret">marks it a secret</see> — in both the raw and the percent-decoded spelling, since an
    /// upstream may echo either. Userinfo is credential material by construction, so all of it is a needle; a query
    /// string is not, so it is filtered by name. The URL itself is never a needle: its host is the fact that says WHICH
    /// endpoint answered, which is the point of surfacing the error at all.
    ///
    /// <para>ACCEPTED RESIDUAL: a gateway that carries its key in the URL PATH (<c>https://gw.example/v1/&lt;key&gt;/chat</c>)
    /// is not covered. A path segment has no name to read, so the only rules available are "mask every segment" —
    /// which strikes the route an operator reads the error for, and every <c>/v1/</c> beside it — or a shape guess,
    /// which is a fresh leak the moment a gateway picks a shape it doesn't match. Userinfo and a marked query
    /// parameter are self-declaring; a path segment is not, so it stays out until a credential can say so itself.</para>
    /// </summary>
    public static IEnumerable<string> FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) yield break;

        foreach (var part in parsed.UserInfo.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
            yield return Uri.UnescapeDataString(part);
        }

        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');

            if (separator < 0 || !MarksASecret(pair[..separator])) continue;

            yield return pair[(separator + 1)..];
            yield return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
    }

    /// <summary>Everything a resolved model credential presents: its api key, and the credential parts of its base URL.</summary>
    public static IEnumerable<string> FromCredential(ResolvedModelCredential? credential)
    {
        if (credential?.ApiKey is { } apiKey) yield return apiKey;

        foreach (var needle in FromUrl(credential?.BaseUrl)) yield return needle;
    }

    /// <summary>
    /// Everything an outgoing request presents: each header whose name <see cref="MarksASecret">marks it a secret</see>
    /// (<c>Authorization</c>, <c>x-api-key</c>, <c>api-key</c>, <c>PRIVATE-TOKEN</c>) and the credential parts of its URL.
    /// A <c>scheme credential</c> header value (<c>Bearer sk-…</c>) yields the credential alone: the scheme word is not a
    /// secret, and striking the credential is what leaves an echoed <c>Authorization=Bearer ***</c> legible.
    /// </summary>
    public static IEnumerable<string> FromRequest(HttpRequestMessage request)
    {
        foreach (var header in request.Headers.Where(header => MarksASecret(header.Key)))
        {
            foreach (var value in header.Value) yield return CredentialOf(value);
        }

        foreach (var needle in FromUrl(request.RequestUri?.OriginalString)) yield return needle;
    }

    /// <summary>The <paramref name="needles"/> long enough to be one (<see cref="MinimumLength"/>), de-duplicated.</summary>
    public static IReadOnlyList<string> Usable(IEnumerable<string?> needles) => needles.OfType<string>().Where(needle => needle.Length >= MinimumLength).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>A redactor over the <see cref="Usable"/> <paramref name="needles"/>; <see cref="SecretRedactor.None"/> when none is.</summary>
    public static SecretRedactor Redactor(IEnumerable<string?> needles) => Usable(needles) is { Count: > 0 } usable ? new SecretRedactor(usable) : SecretRedactor.None;

    /// <summary>
    /// <paramref name="text"/> — an answer an upstream gave a request: an error body, a refusal message — with every
    /// <see cref="Usable"/> credential in <paramref name="presented"/> struck wherever the upstream echoed it. For the call
    /// sites that read a refusal's body THEMSELVES (a raw-HTTP provider call) and hold the token they sent; null stays null.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Strike(string? text, IEnumerable<string?> presented) => text is null ? null : Redactor(presented).Redact(text);

    private static string CredentialOf(string headerValue)
    {
        var separator = headerValue.IndexOf(' ');

        return separator < 0 ? headerValue : headerValue[(separator + 1)..].Trim();
    }
}
