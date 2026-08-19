using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The COMPLETENESS STATEMENT for one facet of a workflow run's record: what it is expected to contain, what is
/// present, what is known-missing, and the verdict those three add up to. It is what makes "the run's data is
/// complete" a claim someone can check rather than an impression.
///
/// <para><b>What it does when it cannot tell, which is the whole reason the table exists.</b>
/// <see cref="Verdict"/> answers exactly one question — may this facet of the record be read as complete? — and only
/// the two states <see cref="WorkflowRunCaptureCompletenessExtensions.IsStrictlyReadable"/> already names answer yes.
/// <see cref="WorkflowRunCaptureCompleteness.Exact"/> and <see cref="WorkflowRunCaptureCompleteness.RedactedExact"/>
/// mean complete (a redacted record is still a whole one); <see cref="WorkflowRunCaptureCompleteness.Partial"/> means
/// something is known-missing, <see cref="WorkflowRunCaptureCompleteness.Unavailable"/> that it was never captured,
/// <see cref="WorkflowRunCaptureCompleteness.Corrupt"/> that it is present and unreadable, and
/// <see cref="WorkflowRunCaptureCompleteness.LegacyUnknown"/> is the INDETERMINATE arm — nobody could establish what
/// should be here. An indeterminate is an <see cref="ExpectedRecordCount"/> of null, and the database refuses either
/// complete verdict over it, because a manifest that read complete when it could not check would have converted an
/// unknown into a false assurance: strictly worse than having no manifest. The DIRECTION is enforced, not the
/// spelling — a producer that cannot tell may say LegacyUnknown, or Partial if it also knows some span is missing, and
/// no constraint can pick between two honest not-complete answers.</para>
///
/// <para><b>Computable without scanning a run's records.</b> These counters are MATERIALIZED by the producers that
/// capture, never derived by a query on read: a verdict costs the two counts on the row, whether an expectation was
/// stated at all, and one partial-index probe for an open <see cref="WorkflowRunCaptureGap"/>. A scan-based definition
/// would cost a COUNT per plane per run and be unevaluatable exactly where it matters — the native-record and
/// log-segment planes grow with harness traffic. What counters cannot establish is whether a producer died between
/// writing a record and advancing the counter; both halves of that window fail closed, leaving present below expected
/// or the expectation unstated, and neither can read as complete.</para>
///
/// <para><b>And the residue that choice accepts.</b> Both counts are the PRODUCER'S declarations, and nothing compares
/// them to the planes they describe — a writer that declares nothing expected, nothing present and states Exact over a
/// facet holding five hundred rows is refused by nothing here, because checking it IS the scan this definition exists
/// to avoid. What the database does hold is everything reachable without that scan: an indeterminate cannot read as
/// complete, neither can a shortfall, and a known-missing span un-completes the statement whichever order the gap and
/// the claim arrive in.</para>
///
/// <para><b>One row per facet, and no run-level roll-up column.</b> A single count summed across facets cancels: three
/// missing native records against three surplus model calls would satisfy present &gt;= expected while the record was
/// plainly incomplete. The consequence is stated rather than papered over — folding these rows into one answer for a
/// run is a LATER, deliberate slice, and that fold must treat a facet with no row as indeterminate, which is precisely
/// why no terminal decision, completion assessment, planner, oracle or router reads this table in this slice.</para>
///
/// <para>Nothing produces or reads a row here yet.</para>
/// </summary>
public sealed class WorkflowRunDataManifest : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>Tenant scope on every statement.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The run whose record this statement is about, proved by a composite foreign key.</summary>
    public Guid WorkflowRunId { get; set; }

    /// <summary>Which part of the record the statement covers, named in <see cref="WorkflowRunDataOwnerKinds"/> so it matches a gap's subject exactly. Unique per run.</summary>
    public string Facet { get; set; } = string.Empty;

    /// <summary>
    /// How many records this facet is expected to contain. NULL is the INDETERMINATE state and the pivot of the whole
    /// table: it means nobody could establish an expectation, and the database refuses a complete verdict over it.
    /// It is nullable rather than defaulted to zero precisely because zero is a determinate claim — "this facet is
    /// expected to be empty" — and reading an unknown as that claim is the assurance this plane exists to refuse.
    /// </summary>
    public long? ExpectedRecordCount { get; set; }

    /// <summary>How many are durably present, advanced by the producer that admitted them.</summary>
    public long PresentRecordCount { get; set; }

    /// <summary>
    /// How many spans of this facet are known-missing. It may not sit BELOW the open gaps already rowed for the facet;
    /// above is admitted, because a producer that knows of more missing than it has rowed is erring toward incomplete.
    /// When a gap for this facet is recorded the database RECONCILES this to the open spans the plane holds rather than
    /// incrementing it — an increment lands one under a floor that already counts the whole statement, and the refusal
    /// that follows destroys every gap in an honest multi-row admission.
    /// </summary>
    public long KnownMissingCount { get; set; }

    /// <summary>
    /// The verdict, in the shared six-state capture vocabulary rather than a parallel one. It defaults to
    /// <see cref="WorkflowRunCaptureCompleteness.LegacyUnknown"/> — a statement nobody filled in must not read as a
    /// complete one, and the enum's own default would otherwise be Exact.
    /// </summary>
    public WorkflowRunCaptureCompleteness Verdict { get; set; } = WorkflowRunCaptureCompleteness.LegacyUnknown;

    /// <summary>Advances by exactly one per write, including the writes the database makes when a gap downgrades this statement.</summary>
    public long Revision { get; set; } = 1;

    /// <summary>The persisted data-contract version of this row's shape.</summary>
    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public uint Xmin { get; set; }
}
