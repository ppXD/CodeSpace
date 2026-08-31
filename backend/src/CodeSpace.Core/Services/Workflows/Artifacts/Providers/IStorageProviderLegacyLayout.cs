using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A provider that can name blobs written BEFORE the CAS plane existed, so a report-only pass can ask whether the
/// rows that predate <c>artifact_location</c> are still where their own records say they are.
///
/// <para>A sibling of <see cref="IStorageProviderModule"/> (Rule 7): almost no destination has a pre-CAS population,
/// and one that does not must not have to answer for one.</para>
/// </summary>
public interface IStorageProviderLegacyLayout
{
    /// <summary>
    /// The object key this provider's pre-CAS layout gives the bytes named by <paramref name="sha256"/>, or null when
    /// the layout does not name them.
    ///
    /// <para>Null covers BOTH failures on purpose, because they are one answer to the caller: the digest is not one
    /// this layout can place, or the key it derives does not resolve to <paramref name="recordedLocator"/> — the
    /// locator the row already carries, and the only ground truth a candidate layout can be checked against. A layout
    /// that cannot reproduce it is a key-mapping bug, and minting rows from it would name bytes that are not there.</para>
    /// </summary>
    string? ResolveLegacyObjectKey(JsonElement nonSecretConfiguration, string sha256, string recordedLocator);
}
