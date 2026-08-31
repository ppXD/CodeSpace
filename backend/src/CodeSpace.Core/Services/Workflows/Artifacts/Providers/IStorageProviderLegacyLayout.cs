using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A provider that can name blobs written BEFORE the CAS plane existed. The report-only survey uses the mapping to
/// establish adoption evidence; phase two combines it with provider-neutral StreamingRead and HealthProbe to mint
/// sidecar CAS observations without changing an immutable legacy row or its reader. HealthProbe is part of this
/// contract because a Missing or thrown per-object answer is safe to record only after the destination itself answers.
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
