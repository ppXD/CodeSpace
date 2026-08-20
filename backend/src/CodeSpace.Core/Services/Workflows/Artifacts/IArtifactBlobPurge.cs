namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Optional sibling capability of <see cref="IArtifactBlobBackend"/> (Rule 7 / ISP — never a widening of it): removal
/// of bytes this backend placed. It is a separate interface because removal is the one operation a transport can
/// legitimately not have — a write-once archive tier, a mirror the deployment only reads — and a backend that cannot
/// remove bytes must stay a valid backend rather than acquire a method it has to throw from.
///
/// <para><b>What implementing it changes.</b> Exactly one thing: an offloaded artifact on this backend becomes
/// reapable. <c>ArtifactRetentionReaper</c> feature-detects this interface off the injected
/// <see cref="IArtifactBlobBackend"/> and, when it is absent, settles every offloaded declaration
/// <c>Indeterminate</c> — terminal, and it means the artifact is kept with its bytes. That is the same fail-closed
/// default the whole retention ledger is built on, so not implementing it is a supported choice.</para>
///
/// <para><b>Why the outcome is a value and not an exception.</b> The reaper has to tell three cases apart to settle a
/// declaration correctly, and two of them are ordinary: bytes removed and bytes already absent are both success (the
/// second is precisely the state a sweep that crashed after its byte delete leaves behind), while a refusal must leave
/// the declaration live and retryable. An exception would collapse the first two into the third.</para>
/// </summary>
public interface IArtifactBlobPurge
{
    /// <summary>
    /// Remove the bytes <paramref name="storageUrl"/> names. Never throws for an absent object — that is the
    /// <see cref="ArtifactBlobPurgeOutcome.AlreadyGone"/> this returns instead, and it is what makes the reaper's
    /// byte-delete idempotent across a crash. Anything the backend will not or cannot do is
    /// <see cref="ArtifactBlobPurgeOutcome.Refused"/>.
    /// </summary>
    Task<ArtifactBlobPurgeOutcome> DeleteAsync(string storageUrl, CancellationToken cancellationToken);
}

/// <summary>What one byte-removal attempt did. Both <see cref="Deleted"/> and <see cref="AlreadyGone"/> mean the bytes are not there any more, which is all the caller needs to proceed.</summary>
public enum ArtifactBlobPurgeOutcome
{
    /// <summary>The bytes were there and are now removed.</summary>
    Deleted = 1,

    /// <summary>The bytes were already absent when this call looked. Success: the caller's goal is the absence, not the act.</summary>
    AlreadyGone = 2,

    /// <summary>The backend would not or could not remove them. The caller must leave every row that names them intact.</summary>
    Refused = 3,
}
