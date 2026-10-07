using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// <see cref="IPackCloneUrlProtector"/> over the platform's credential encryptor (<see cref="IPayloadEncryptor"/>, whose
/// key ring every pod shares) — the same storage a model API key or a webhook secret has. One rule decides whether a
/// URL carries a credential: removing its userinfo changes it. That covers a token pasted as the password and one
/// pasted as the user alone.
///
/// <para>The WHOLE URL is sealed, not just its userinfo: removing the userinfo also normalizes the URL (host case,
/// escaping), so recomposing it would clone a different string than the import cloned.</para>
/// </summary>
public sealed class PackCloneUrlProtector : IPackCloneUrlProtector, ISingletonDependency
{
    private const string UnreadableSourceMessage = "This pack's saved source credential can no longer be read; import it again from a URL with a current token.";

    private readonly IPayloadEncryptor _encryptor;

    public PackCloneUrlProtector(IPayloadEncryptor encryptor) { _encryptor = encryptor; }

    public (string Url, string? EncryptedCloneUrl) Seal(string cloneUrl)
    {
        var url = WithoutCredential(cloneUrl);

        return (url, url == cloneUrl ? null : _encryptor.Encrypt(cloneUrl));
    }

    public string CloneUrlOf(Pack pack)
    {
        if (pack.EncryptedCloneUrl is null) return pack.Url!;

        try
        {
            return _encryptor.Decrypt(pack.EncryptedCloneUrl);
        }
        catch (CryptographicException)
        {
            // The inner exception is dropped on purpose: it can quote the payload, and this message reaches the API.
            throw new PackImportException(UnreadableSourceMessage);
        }
    }

    /// <summary><paramref name="url"/> with its userinfo removed; unchanged when it carries none (or is not an absolute URL). Pure + internal so the read model and the backfill share the writer's rule.</summary>
    internal static string WithoutCredential(string url) => RemoteTipResolver.SanitizeUrl(url);

    /// <summary>True when <paramref name="url"/> carries userinfo — the backfill's candidate test. Pure + internal so it is unit-pinned against <see cref="Seal"/>.</summary>
    internal static bool CarriesCredential(string url) => WithoutCredential(url) != url;
}
