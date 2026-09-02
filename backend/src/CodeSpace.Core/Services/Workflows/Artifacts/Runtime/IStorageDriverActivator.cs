using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// The last step of opening a storage driver: hand one provider factory a configuration snapshot and, where the
/// provider needs one, an activated credential, and get back either a lease or a closed failure reason.
///
/// <para>Everything above it differs by where the configuration CAME from - a persisted profile revision the runtime
/// pinned, or configuration an operator is still typing into Settings and has not saved. Everything below it is
/// identical, and it is the intricate half: a provider factory may refuse, cancel, throw, or hand back null, and each
/// of those has to become a reason an operator can act on without ever quoting provider text that might carry a
/// secret. Sharing it is what keeps two callers' answers from drifting into two different vocabularies for the same
/// provider fault.</para>
/// </summary>
public interface IStorageDriverActivator : IScopedDependency
{
    /// <summary>
    /// Opens a driver for <paramref name="snapshot"/> through <paramref name="factory"/>.
    ///
    /// <para>Takes OWNERSHIP of <paramref name="credential"/> and disposes it before returning, on every path
    /// including cancellation and refusal. A factory may materialize its provider SDK credential while
    /// <c>CreateAsync</c> runs and must never retain the handle, so the handle's lifetime is exactly this call.
    /// Pass null for a provider that needs no secret.</para>
    /// </summary>
    ValueTask<StorageRuntimeDriverResolution> ActivateAsync(IArtifactStorageDriverFactory factory, StorageProfileSnapshot snapshot, StorageCredentialHandle? credential, CancellationToken cancellationToken);
}
