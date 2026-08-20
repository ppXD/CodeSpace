using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>
/// A SIBLING of <c>IArtifactStore</c> (Rule 7), not a widening of it: the write that also declares the bytes for
/// retention. Every existing <c>PutAsync</c> caller is untouched and its bytes stay permanently unreapable, which is
/// the safe default this seam exists to preserve.
/// </summary>
public interface IArtifactRetentionWriter : IScopedDependency
{
    /// <summary>
    /// Store <paramref name="request"/>'s bytes exactly as <c>IArtifactStore.PutAsync</c> would, and — only when this
    /// call is the write that INSERTED the artifact row, and only when the bytes went somewhere the reaper can remove
    /// them from (inline in the row, or the local blob backend, but never a routed storage profile) — mint the retention
    /// declaration in the same transaction as that insert.
    ///
    /// <para>Atomic-with-the-insert is what makes the declaration sound: nothing can dedup against a row that has not
    /// committed, so no other writer can already be holding this id when the declaration appears. A dedup hit declares
    /// NOTHING and revokes any declaration already on the row, because the store hands the same id to that later writer
    /// and it may reference the artifact from a place the reference oracle cannot see.</para>
    /// </summary>
    Task<ArtifactRetentionWrite> PutDeclaredAsync(ArtifactRetentionWriteRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A declaring write. <paramref name="HolderKind"/> and <paramref name="HolderId"/> record what the producer said it
/// was about to write, for diagnosing a collection after the fact — the reaper never treats them as the reference
/// check.
/// </summary>
public sealed record ArtifactRetentionWriteRequest(Guid TeamId, ReadOnlyMemory<byte> Bytes, string ContentType, ArtifactRetentionClass RetentionClass, string HolderKind, Guid HolderId);

/// <summary>
/// The stored artifact plus whether a declaration was actually minted. <paramref name="Declared"/> false is the normal,
/// safe outcome for a dedup hit or a routed write — the bytes are simply never reapable.
/// </summary>
public sealed record ArtifactRetentionWrite(Guid ArtifactId, bool Declared);
