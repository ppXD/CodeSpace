using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// The runtime observed on THIS host is not the runtime the campaign froze, so no cell it would produce may join the
/// same paired evidence census. Raised at cell admission, at execution, on recovery, and at seal — every point where
/// a different process could otherwise pay for a cell that measures something else.
///
/// <para>It names ONE field (the first divergence in canonical order) rather than a whole diff: an operator needs to
/// know WHICH substitution happened — a swapped CLI binary, a rotated key, a repointed gateway, an unconfinable
/// worker — and the two digests let them prove it against the persisted protocol row. The field path is a canonical
/// JSON path (<c>manifest.runner.buildIdentity</c>, <c>manifest.harnesses[0].binarySha256</c>), never a value: a
/// drifted credential must not leak either side's material into a message that reaches a log or a response body.</para>
/// </summary>
public sealed class RuntimeManifestDriftException : InvalidOperationException, IFailure
{
    public RuntimeManifestDriftException(string field, string frozenDigest, string observedDigest)
        : base($"The qualification runtime manifest drifted at '{field}': the campaign froze {frozenDigest} but this host observes {observedDigest}. The campaign cannot continue on a runtime it did not measure.")
    {
        Field = field;
        FrozenDigest = frozenDigest;
        ObservedDigest = observedDigest;
    }

    /// <summary>Canonical JSON path of the first drifted field — e.g. <c>manifest.harnesses[1].binarySha256</c>.</summary>
    public string Field { get; }

    /// <summary>The manifest digest frozen on the protocol row.</summary>
    public string FrozenDigest { get; }

    /// <summary>The manifest digest this host observes now.</summary>
    public string ObservedDigest { get; }

    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.Internal;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["field"] = Field, ["frozenDigest"] = FrozenDigest, ["observedDigest"] = ObservedDigest };
}
