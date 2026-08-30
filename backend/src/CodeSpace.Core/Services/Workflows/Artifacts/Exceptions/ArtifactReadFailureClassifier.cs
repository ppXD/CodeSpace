using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;

/// <summary>
/// What ONE exception raised while reading stored bytes MEANS to the storage plane. A pure lookup, and nothing else:
/// no catching, no policy, no logging.
///
/// <para>The table lived only inside the BOUNDED read, so the whole-object read had no verdict at all and let a
/// digest mismatch escape as a bare <c>InvalidOperationException</c> — which the failure classifier reads as the
/// caller's fault, so one rotted output answered an operator's run-detail read with "your request was malformed".</para>
///
/// <para>Only the verdict is shared. The three readers that consult it owe their callers three DIFFERENT things for
/// the same fact — a bounded read REPORTS it, an execution read FAILS CLOSED on it, a display read SHEDS it onto the
/// pointer — so each keeps its own try/catch. A shared catch-and-decide helper is one refactor away from a workflow
/// branching on a pointer object as if it were data, which is worse than the crash this replaced.</para>
///
/// <para>An exception the table does not name is NOT claimed, so a caller's <c>when</c> filter lets it propagate
/// exactly as it does today. An exception that can ONLY mean a bug, or a caller leaving, must never be laundered into
/// a storage verdict. The test is what the plane can actually RAISE for the fact, not how bug-shaped the type looks:
/// a lane a backend really refuses on is claimed even when its type is a general-purpose one, because dropping it
/// sends the refusal back out untyped — the exact escape this table exists to close.</para>
/// </summary>
public static class ArtifactReadFailureClassifier
{
    public static bool TryClassify(Exception exception, out ArtifactContentUnavailableKind kind)
    {
        var classified = Classify(exception);
        kind = classified ?? default;

        return classified is not null;
    }

    /// <summary>
    /// Exhaustive by CASE over what the plane owns. Order matters: the missing-object and truncated-stream arms sit
    /// above <see cref="IOException"/>, which they derive from, and a verdict the routed plane already reached is
    /// passed through rather than re-decided.
    ///
    /// <para>Deliberately NOT here: <see cref="ArgumentOutOfRangeException"/>. A bad offset makes it a storage-plane
    /// fact for the BOUNDED read alone — nothing hands an offset to a whole-object read, so every whole-object
    /// sighting of it IS a bug — and that arm stays local to <c>ReadRangeCoreAsync</c>.</para>
    ///
    /// <para><see cref="InvalidOperationException"/> looks like its twin and is NOT: it is how a backend refuses a
    /// locator it will not follow. <c>LocalFileArtifactBlobBackend.ResolveUnderRoot</c> raises it for a
    /// <c>storage_url</c> whose scheme this backend cannot serve and for one resolving outside the store root. That is
    /// "the stored copy cannot be produced" — a fact a display read must be able to shed and an execution read must
    /// fail closed on. Unclaimed it escapes untyped, reads to the failure classifier as the caller's fault, and
    /// answers a run-detail read 400.</para>
    ///
    /// <para>Which is why <see cref="ObjectDisposedException"/> needs an arm of its OWN: it derives from that claimed
    /// refusal, so omitting it is not refusing it. A stream read after its lease was let go is our defect, and dressed
    /// as a storage lane it sends an operator to restore a healthy destination while the bug keeps rotting cells.</para>
    /// </summary>
    private static ArtifactContentUnavailableKind? Classify(Exception exception) => exception switch
    {
        ArtifactContentUnavailableException typed => typed.Kind,
        FileNotFoundException or DirectoryNotFoundException => ArtifactContentUnavailableKind.PhysicalObjectMissing,
        UnauthorizedAccessException => ArtifactContentUnavailableKind.AccessDenied,
        InvalidDataException or EndOfStreamException or JsonException => ArtifactContentUnavailableKind.IntegrityFailure,
        ObjectDisposedException => null,
        InvalidOperationException => ArtifactContentUnavailableKind.IntegrityFailure,
        IOException => ArtifactContentUnavailableKind.BackendUnavailable,
        _ => null,
    };
}
