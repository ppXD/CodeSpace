using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Workflows;

namespace CodeSpace.Core.Services.Workflows.Callbacks;

/// <summary>
/// How a callback wait's token is kept. The token is a bearer credential — the callback route is anonymous, so whoever
/// presents it resumes the run with a body of their choosing. Its row keeps it three ways:
/// <list type="bullet">
/// <item><c>token_digest</c>, its SHA-256, which a resume looks the wait up by (the way invitation and reset tokens are kept);</item>
/// <item><c>token_ciphertext</c>, the token AES-GCM encrypted for the one reader allowed to see it again: the RunsControl-gated reveal;</item>
/// <item><c>token</c>, the token as minted — kept for this release only, because a pod on the previous build looks the wait
/// up by comparing that column with the presented token. A later release blanks it once no such pod can run.</item>
/// </list>
///
/// <para>The encryption key is the deployment's variable master key (<c>Variables:MasterKey</c>), which the environment
/// holds and the database never does. Not Data Protection: its key ring is stored unencrypted in the same database
/// (<c>data_protection_keys</c>) under a public application name, so a database or backup reader could open anything it
/// protects.</para>
///
/// <para>Older row shapes stay readable. A row minted before migration 0248, or parked by a previous-version pod, holds only
/// the token, in the shape <see cref="Mint"/> produces (32 lowercase hex): a resume matches it in the token column, and a
/// reveal returns it as stored. A row whose token column a later release blanked holds only its digest and ciphertext.</para>
/// </summary>
public sealed class WorkflowCallbackTokenSealer : ISingletonDependency
{
    private const int MintedLength = 32;

    private readonly IVariableValueEncryption _encryption;

    public WorkflowCallbackTokenSealer(IVariableValueEncryption encryption) => _encryption = encryption;

    /// <summary>A new token — the same 32-character shape the callback route admits and every earlier version minted — with the two other forms its row stores.</summary>
    public SealedCallbackToken Mint()
    {
        var token = Guid.NewGuid().ToString("N");

        return new SealedCallbackToken(token, Digest(token), Convert.ToBase64String(_encryption.Encrypt(token)));
    }

    /// <summary>
    /// The token a reveal hands out: the ciphertext opened when there is one, else the token column when it holds a minted
    /// token, else null — a row whose token column was blanked and never sealed cannot give it back. A ciphertext sealed
    /// under another master key throws <see cref="CryptographicException"/>.
    /// </summary>
    public string? Open(string storedToken, string? ciphertext)
    {
        if (ciphertext != null) return _encryption.Decrypt(Convert.FromBase64String(ciphertext));

        return PlaintextFormOf(storedToken);
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 token — byte for byte what migration 0248 writes with <c>encode(sha256(convert_to(token, 'UTF8')), 'hex')</c>.</summary>
    public static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// The value a row's token column holds for <paramref name="value"/>: the value itself when it has the minted shape, the
    /// only shape any version writes there for a Callback, else null. So neither a digest (64 characters) nor a blanked
    /// column is ever compared as a token.
    /// </summary>
    public static string? PlaintextFormOf(string value) => IsMintedShape(value) ? value : null;

    private static bool IsMintedShape(string value) => value.Length == MintedLength && value.All(char.IsAsciiHexDigitLower);
}
