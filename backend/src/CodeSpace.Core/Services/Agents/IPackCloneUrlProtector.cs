using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Splits a pack's clone URL into the credential-free URL the pack stores, shows and is identified by, and the sealed
/// URL it clones from. A pasted git URL may embed a token in its userinfo; that URL is what the import clones, and it
/// is the only thing that can clone a private pack again — but <c>pack.url</c> reaches every team member. The split
/// keeps the clone lossless (Sync clones the exact string the import cloned) without the token ever being stored,
/// returned or rendered in plaintext.
/// </summary>
public interface IPackCloneUrlProtector
{
    /// <summary>The URL a pack stores for <paramref name="cloneUrl"/> (its userinfo removed; byte-identical when it carries none) and, only when it carried userinfo, the whole URL sealed.</summary>
    (string Url, string? EncryptedCloneUrl) Seal(string cloneUrl);

    /// <summary>The URL to clone <paramref name="pack"/> from: its sealed source decrypted, or its URL when it has none. Throws <see cref="PackImportException"/>, naming neither URL nor ciphertext, when the sealed source can no longer be read.</summary>
    string CloneUrlOf(Pack pack);
}
