using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Diagnostics;

/// <summary>
/// Turns a provider call we are about to describe into something safe to write to a row.
///
/// <para>This is the ONLY way a <see cref="CapturedProviderRequest"/> should come into existence.
/// Callers hand over the request as it really went out — real secret, real credential — and get
/// back the masked form. Masking here rather than at the call site is deliberate: a call site that
/// pre-masks is a call site that can forget, and the forgetting is invisible until the leak is
/// already in the database.</para>
///
/// <para>Three independent rules, because a secret can enter a request three ways. A named
/// credential HEADER is masked by header name; a secret-bearing JSON FIELD is masked by key name,
/// which still holds for a field someone adds next year without telling us; and finally every
/// declared secret VALUE is scrubbed literally wherever it appears, which catches the URL query
/// parameter that neither of the first two rules looks at.</para>
/// </summary>
public static class ProviderCallCapture
{
    /// <summary>What replaces a secret. A fixed marker, so a reader can tell "masked" from "the provider sent an empty value".</summary>
    public const string Mask = "***";

    /// <summary>
    /// Cap for a captured body, request or response. Provider errors are a sentence; an HTML error
    /// page from a misconfigured reverse proxy is a megabyte, and ten attempts of those would make
    /// the diagnostic table larger than everything it diagnoses.
    /// </summary>
    public const int MaxBodyChars = 4000;

    /// <summary>Header names whose value IS a credential. Matched case-insensitively — HTTP header names are.</summary>
    private static readonly IReadOnlySet<string> CredentialHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "PRIVATE-TOKEN", "JOB-TOKEN", "X-Api-Key", "Cookie"
    };

    /// <summary>JSON keys whose value is a secret in every provider payload we send.</summary>
    private static readonly IReadOnlySet<string> SecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "token", "secret", "password", "private_token", "access_token", "refresh_token", "api_key", "authorization"
    };

    public static CapturedProviderRequest CaptureRedacted(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, IReadOnlyCollection<string?> secrets) => new()
    {
        Method = method,
        Url = ScrubSecrets(url, secrets)!,
        Body = Clamp(ScrubSecrets(MaskSecretJsonFields(body), secrets)),
        Headers = MaskCredentialHeaders(headers, secrets)
    };

    /// <summary>Clamp a provider-supplied body to <see cref="MaxBodyChars"/>, marking that we cut it so nobody debugs a truncation as a malformed response.</summary>
    public static string? Clamp(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxBodyChars) return text;

        return text[..MaxBodyChars] + $"… [truncated, {text.Length} chars total]";
    }

    /// <summary>Walk the <see cref="Exception.InnerException"/> chain for the SDK exception a provider knows how to read. Translation layers wrap, so the shape we want is rarely the one we caught.</summary>
    public static T? FindInChain<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is T match) return match;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> MaskCredentialHeaders(IReadOnlyDictionary<string, string> headers, IReadOnlyCollection<string?> secrets)
    {
        return headers.ToDictionary(h => h.Key, h => CredentialHeaders.Contains(h.Key) ? Mask : ScrubSecrets(h.Value, secrets)!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mask by KEY NAME inside a JSON body, recursively — GitHub nests the webhook secret under
    /// <c>config.secret</c>, so a top-level pass would miss it. Non-JSON bodies pass through
    /// untouched; the literal scrub below is what covers them.
    /// </summary>
    private static string? MaskSecretJsonFields(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;

        try
        {
            var root = JsonNode.Parse(body);
            if (root == null) return body;

            MaskNode(root);
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static void MaskNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToList())
            {
                if (SecretKeys.Contains(key)) obj[key] = Mask;
                else if (value != null) MaskNode(value);
            }

            return;
        }

        if (node is not JsonArray array) return;

        foreach (var item in array)
        {
            if (item != null) MaskNode(item);
        }
    }

    /// <summary>
    /// Last line of defence: replace each declared secret VALUE wherever it appears. Catches the
    /// places structure cannot reach — a <c>?private_token=</c> query parameter, a secret echoed
    /// back inside an error string.
    /// </summary>
    private static string? ScrubSecrets(string? text, IReadOnlyCollection<string?> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var scrubbed = text;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret)) scrubbed = scrubbed.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return scrubbed;
    }
}
