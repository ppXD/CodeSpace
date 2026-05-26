using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Providers.GitHub;

public sealed class GitHubSignatureVerifier
{
    private const string HeaderName = "X-Hub-Signature-256";
    private const string ExpectedPrefix = "sha256=";

    public bool Verify(string body, IReadOnlyDictionary<string, string> headers, string secret)
    {
        if (!TryFindHeader(headers, HeaderName, out var rawSignature)) return false;
        if (!rawSignature.StartsWith(ExpectedPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var providedHex = rawSignature.Substring(ExpectedPrefix.Length).ToLowerInvariant();
        var computedHex = ComputeSignatureHex(body, secret);

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(providedHex), Encoding.ASCII.GetBytes(computedHex));
    }

    private static bool TryFindHeader(IReadOnlyDictionary<string, string> headers, string name, out string value)
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

    private static string ComputeSignatureHex(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
