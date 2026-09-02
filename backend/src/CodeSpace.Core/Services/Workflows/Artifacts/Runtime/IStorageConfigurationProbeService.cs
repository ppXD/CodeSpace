using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Answers whether a destination an operator has just described actually works - before CodeSpace records it.
///
/// <para>Sibling of <see cref="IStorageProfileProbeService"/>, not a mode of it (Rule 7). That one is pinned to a
/// persisted <c>(team, profile, revision)</c> and asks whether a destination the plane already has bytes in still
/// answers. This one holds no identity at all: nothing about it is in the database, and nothing about it will be.</para>
///
/// <para>WHY IT HAS TO EXIST: <c>storage_profile</c> has no delete - the row's own trigger refuses one, and every
/// stored object's location stamps the exact revision it was written under, so a revision must outlive whatever it
/// wrote. Without this seam the only way to find out whether a key works is to save a credential and a profile and
/// probe those, which means a mistyped secret leaves behind two rows an operator can never remove and must instead
/// learn a lifecycle vocabulary to reason about. With it, a wrong key costs an edit.</para>
///
/// <para>It admits the configuration and the secret through the SAME gates the save path uses, so a rejection here
/// and a rejection at save can never disagree, and it answers in the same closed vocabulary the saved-profile probe
/// answers in. It never persists, never logs the secret, and never returns provider text.</para>
/// </summary>
public interface IStorageConfigurationProbeService : IScopedDependency
{
    Task<StorageConfigurationProbeResult> ProbeAsync(StorageConfigurationProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One unsaved destination: which provider, its non-secret configuration, and - for a provider that needs one - the
/// plaintext secret, held only for the lifetime of the call.
/// </summary>
public sealed record StorageConfigurationProbeRequest(string ProviderTypeKey, JsonElement NonSecretConfig, JsonElement? Secret);
