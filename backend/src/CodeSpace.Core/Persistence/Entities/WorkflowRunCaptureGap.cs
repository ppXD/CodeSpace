using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// ONE known-missing span of a workflow run's record — the place a producer records "I know I missed something here".
///
/// <para>It exists because an invisible gap is indistinguishable from no gap. A capture plane that hits a configured
/// bound, has a durable write refused, re-attaches to a torn stream, or is handed a frame it cannot read has, today,
/// only one way to say so: a silence that reads exactly like nothing having happened. This row is what replaces that
/// silence, which is also what makes <see cref="WorkflowRunDataManifest"/> worth computing — a completeness statement
/// over a plane that cannot represent its own absences would report complete because nothing said otherwise.</para>
///
/// <para><b>Append-only, and the DELETE refusal is load-bearing.</b> Nothing about a gap may be restated, and no gap
/// may be deleted: a removable gap makes a complete manifest reachable by deleting the evidence for it. The price is
/// stated rather than hidden — a gap row is not prunable and its run cannot be deleted while it exists, the same dead
/// end <see cref="WorkflowRunNativeRecord"/> already accepted. It is affordable here for a reason that does not hold
/// there: this plane's row count is bounded by the number of spans a run NOTICED were missing, not by its traffic.</para>
///
/// <para><b>Resolution is the one axis that may be filled, exactly once.</b> Some spans are never coming back — bytes
/// past a capture cap were never taken from anyone — and some are: a torn re-attach whose source still holds the lines
/// is captured on the next pass. A gap that could never close would make the manifest fail-ALWAYS rather than
/// fail-closed, and a verdict nothing can reach is not a verdict. Every gap is nevertheless BORN
/// <see cref="CaptureGapResolution.Open"/>, so a span cannot be recovered in the same breath as it is recorded and
/// thereby never appear as missing. A recovery must CITE what now covers the span; what no schema can check is whether
/// the cited row's bytes actually do.</para>
///
/// <para>Keyed as the tool-call plane is, which is why <see cref="WorkflowRunId"/> is non-nullable: a gap noticed by a
/// STANDALONE Agent Run has no row here yet, the same named gap the harness execution plane carries.
/// <see cref="SubjectId"/>, <see cref="StreamId"/> and <see cref="RecoveredById"/> are SOFT references — the rows they
/// name arrive through bounded sweepers, and refusing a gap because its subject is not projected yet would be the one
/// answer this table must never give.</para>
///
/// <para><b>Who records one.</b> Exactly one producer exists: the native-record capture plane
/// (<c>NativeRecordPlane</c>) records a <see cref="CaptureGapReason.WriteRefused"/> span when a batch of captured frames
/// is refused durable storage. It is recorded through <c>IRunDataCompletenessWriter</c> on a commit of its OWN, never
/// together with a claim about the record: the bad news has to survive whatever happens to the claim it contradicts,
/// and a shared transaction would let a refused statement take the gap down with it. The other three reasons are
/// representable and unproduced — no plane yet notices its own bound, its own torn re-attach or its own unreadable
/// frame — and nothing READS or folds a gap: no completion, terminal decision, planner, oracle or router is aware of
/// one.</para>
/// </summary>
public sealed class WorkflowRunCaptureGap : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>Tenant scope on every recorded gap.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The owning workflow run, proved by a composite foreign key as the tool-call plane's is.</summary>
    public Guid WorkflowRunId { get; set; }

    /// <summary>
    /// WHAT was being captured, named in <see cref="WorkflowRunDataOwnerKinds"/> rather than a parallel vocabulary, so
    /// a gap can always be matched to the plane whose record is missing — which is what lets the manifest count it
    /// against the right facet instead of only suppressing the whole run.
    /// </summary>
    public string SubjectKind { get; set; } = string.Empty;

    /// <summary>The specific owner the span belongs to when one is known; null for a gap that covers the plane rather than a single row.</summary>
    public string? SubjectId { get; set; }

    /// <summary>The capture stream the missing span sits in. Required by an ordinal or byte-offset range, because a position with nothing to be a position IN is a coordinate nobody can locate.</summary>
    public Guid? StreamId { get; set; }

    /// <summary>Which channel of the stream, where the subject has channels at all. Reuses the native-record channel vocabulary so one reader recognises both.</summary>
    public NativeRecordChannel? Channel { get; set; }

    /// <summary>Which coordinate system <see cref="RangeStart"/>/<see cref="RangeEnd"/>/<see cref="RangeStartedAt"/>/<see cref="RangeEndedAt"/> are stated in. The four arms are exhaustive and mutually exclusive in the database, so a producer never invents a fifth and a reader never guesses which columns mean anything.</summary>
    public CaptureGapRangeKind RangeKind { get; set; } = CaptureGapRangeKind.Unbounded;

    /// <summary>First missing position under <see cref="CaptureGapRangeKind.Ordinal"/> or <see cref="CaptureGapRangeKind.ByteOffset"/>; null under the other two.</summary>
    public long? RangeStart { get; set; }

    /// <summary>Last missing position, inclusive of the span's own extent. NULL is deliberately legal: "from here on, and I do not know how much" is the honest shape of a torn re-attach, and demanding a bound would make a producer guess one.</summary>
    public long? RangeEnd { get; set; }

    /// <summary>Start of the missing wall-clock window under <see cref="CaptureGapRangeKind.Time"/> — the coordinate left when a producer knows WHEN it was blind but not where in a stream.</summary>
    public DateTimeOffset? RangeStartedAt { get; set; }

    /// <summary>End of the missing window; null while the span is still open-ended.</summary>
    public DateTimeOffset? RangeEndedAt { get; set; }

    /// <summary>
    /// The producer's typed reason. Required at every construction site because the enum has no honest default — an
    /// unset reason would persist as some other producer's reason, and the vocabulary deliberately has no 'Unknown'
    /// member for it to fall into: a producer that cannot say why it missed a span has not finished observing it.
    /// </summary>
    public required CaptureGapReason Reason { get; set; }

    /// <summary>Free-text detail behind <see cref="Reason"/> — which bound, which refusal — for a human reading one row. Never parsed, and never a second reason vocabulary.</summary>
    public string? ReasonDetail { get; set; }

    /// <summary>Which producer noticed, in the same capture-source vocabulary the other planes use (in-process, harness-native, controlled-proxy).</summary>
    public string CaptureSource { get; set; } = "unknown";

    /// <summary>When the producer noticed the span was missing — not when the span happened, which <see cref="RangeStartedAt"/> states when it is known at all.</summary>
    public DateTimeOffset NoticedAt { get; set; }

    /// <summary>Whether the span is still missing. Filled once, one direction, and never at birth.</summary>
    public CaptureGapResolution Resolution { get; set; } = CaptureGapResolution.Open;

    /// <summary>When the span stopped being missing. Present exactly with the rest of the recovery citation.</summary>
    public DateTimeOffset? RecoveredAt { get; set; }

    /// <summary>The owner noun of whatever now covers the span; required by a recovery, because an uncited Recovered is an unattributable claim that silently unblocks a complete verdict.</summary>
    public string? RecoveredByKind { get; set; }

    /// <summary>The identity of that row, in <see cref="RecoveredByKind"/>'s own terms. A soft reference: it makes the claim attributable, never verified.</summary>
    public string? RecoveredById { get; set; }

    /// <summary>The persisted data-contract version of this row's shape.</summary>
    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The coordinate system a missing span is stated in. Exhaustive and mutually exclusive: exactly one arm of the range
/// CHECK admits any given row, so there is no combination of bounds a writer can land in that means nothing.
/// </summary>
public enum CaptureGapRangeKind
{
    /// <summary>Positions within a capture stream, as the native-record plane numbers them. Requires a stream.</summary>
    Ordinal,

    /// <summary>Byte offsets within a source stream, as the log-capture plane numbers them. Requires a stream.</summary>
    ByteOffset,

    /// <summary>A wall-clock window — the coordinate left when capture knows when it was blind but not where.</summary>
    Time,

    /// <summary>No coordinate available at all. Still an honest gap, and the one a producer must not have to fake a range to record.</summary>
    Unbounded,
}

/// <summary>
/// Why a span is missing, closed on purpose. Each member is a real thing a producer does when it cannot capture: there
/// is no 'Unknown' arm, because a reason column that admitted one would collect every gap nobody wanted to classify
/// and the plane would be back to a silence with extra columns.
/// </summary>
public enum CaptureGapReason
{
    /// <summary>A configured bound stopped the capture — a size cap, a count cap, a time cap. The bytes past the cut were never taken from the source.</summary>
    BoundExceeded,

    /// <summary>The durable write was refused — storage, quota, admission. The frame reached capture and did not reach storage.</summary>
    WriteRefused,

    /// <summary>Capture re-attached and the span between the old and new positions was never delivered to anyone.</summary>
    ReattachTorn,

    /// <summary>The frame arrived and could not be read — undecodable bytes, a truncated frame, a torn record.</summary>
    FrameUnreadable,
}

/// <summary>Whether the span is still missing. Filled exactly once, in one direction, and never at birth.</summary>
public enum CaptureGapResolution
{
    /// <summary>Still missing. The state every gap is born in, and the one that blocks a complete manifest for its run.</summary>
    Open,

    /// <summary>The span is now covered by the cited row. It stops blocking completeness; it does not stop being a recorded fact.</summary>
    Recovered,
}
