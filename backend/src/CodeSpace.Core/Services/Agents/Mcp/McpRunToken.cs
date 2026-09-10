using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The per-run MCP capability token: a 256-bit CSPRNG opaque string the executor mints at endpoint open and the
/// consumer presents as the connection's FIRST line. The token IS the capability — it's never logged and never passed
/// on argv; the endpoint validates it server-side before any handler runs. <see cref="Matches"/>
/// uses a constant-time compare so a wrong token leaks no timing oracle (false on a length mismatch, in constant time).
/// It IS persisted, on the run's durable handle, for exactly one reason — a re-attaching worker must re-open the
/// endpoint with the token the detached agent's declaration file already holds (see <c>SandboxHandle.McpRunToken</c>).
/// </summary>
internal static class McpRunToken
{
    /// <summary>Mint a fresh 256-bit token, base64url-encoded (url-safe, unpadded) so it survives an env var / a single line unaltered.</summary>
    internal static string Mint() => Encode(32);

    /// <summary>
    /// Mint a 128-bit CSPRNG id for a per-run FILESYSTEM PATH segment — the socket directory a run's endpoint binds in,
    /// and the declaration file the run's token is written to. Its whole job is to make those paths UNDERIVABLE from
    /// the run id: the run id is a shared, non-secret identifier (it is in URLs, events and artifacts), so any path
    /// computed from it is a path anything holding the id can compute. Base64url means it is filename-safe on every
    /// platform. Shorter than <see cref="Mint"/> because it rides inside an <c>AF_UNIX</c> path, whose total length is
    /// capped — 128 bits is far past guessable while costing 22 characters.
    /// </summary>
    internal static string MintPathId() => Encode(16);

    private static string Encode(int byteCount)
    {
        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True only when the presented token equals the expected one. Constant-time over the byte content; a length mismatch is false (still constant time per <see cref="CryptographicOperations.FixedTimeEquals"/>'s contract).</summary>
    internal static bool Matches(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(presented));
}
