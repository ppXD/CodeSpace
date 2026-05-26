using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Providers.GitLab;

public sealed class GitLabSignatureVerifier
{
    private const string HeaderName = "X-Gitlab-Token";

    public bool Verify(string body, IReadOnlyDictionary<string, string> headers, string secret)
    {
        if (!TryFindHeader(headers, HeaderName, out var providedToken)) return false;

        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        return CryptographicOperations.FixedTimeEquals(providedBytes, secretBytes);
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
}
