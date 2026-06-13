using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The per-run MCP capability token: a 256-bit CSPRNG opaque string the executor mints at endpoint open and the
/// consumer presents as the connection's FIRST line. The token IS the capability — it's never persisted, never
/// logged, never passed on argv; the endpoint validates it server-side before any handler runs. <see cref="Matches"/>
/// uses a constant-time compare so a wrong token leaks no timing oracle (false on a length mismatch, in constant time).
/// </summary>
internal static class McpRunToken
{
    /// <summary>Mint a fresh 256-bit token, base64url-encoded (url-safe, unpadded) so it survives an env var / a single line unaltered.</summary>
    internal static string Mint()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True only when the presented token equals the expected one. Constant-time over the byte content; a length mismatch is false (still constant time per <see cref="CryptographicOperations.FixedTimeEquals"/>'s contract).</summary>
    internal static bool Matches(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(presented));
}
