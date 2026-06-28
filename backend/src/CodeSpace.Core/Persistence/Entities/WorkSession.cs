using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A long-term WORK-CONTEXT thread — the OUTER container the conversational / iteration layer hangs off.
/// The layering it sits atop:
/// <list type="bullet">
///   <item><b>WorkSession</b> — 長期工作上下文 (context / dialogue / iteration / multi-participant). One thread.</item>
///   <item><c>WorkflowRun</c> — one auditable, replayable, recoverable execution = ONE turn of the thread.</item>
///   <item><c>AgentRun</c> — a unit of work inside an execution.</item>
///   <item>Trace / event — the source of truth (a session's live run status is PROJECTED from runs + decisions,
///         never stored on the session).</item>
/// </list>
///
/// <para>A run links to its session by the FK-free <c>WorkflowRun.SessionId</c> pointer (a run ∈ exactly one
/// session) — NOT a join table — so the binding also rides the existing run→AgentRun FK to every child unit.
/// The session row itself stays thin: it owns the thread's IDENTITY (title / kind / lifecycle) and its rolling
/// CONTEXT (<see cref="Summary"/> / <see cref="ScopeJson"/>); the turns, their decisions, artifacts, cost, and
/// branches all live on the runs.</para>
///
/// <para>Decoupled from <c>Conversation</c> (which is human↔human chat): a WorkSession is "a user ↔ their own
/// workflow runs". A UI may reuse conversation rendering, but the data model does not couple.</para>
/// </summary>
public class WorkSession : IEntity<Guid>, IAuditable
{
    /// <summary>
    /// Max length of <see cref="Title"/> — the single source of truth for the column width: the EF
    /// <c>HasMaxLength</c> and any caller that derives a title (see <c>WorkSessionService.SanitizeTitle</c>)
    /// reference this so a title can never overflow the column. Migration <c>0069</c>'s <c>VARCHAR(256)</c>
    /// must match this literal (pinned by <c>WorkSessionTitleTests</c>).
    /// </summary>
    public const int TitleMaxLength = 256;

    public Guid Id { get; set; }

    /// <summary>Tenancy — every session is scoped to exactly one team (mirrors <c>WorkflowRun.TeamId</c>).</summary>
    public Guid TeamId { get; set; }

    /// <summary>Human-facing thread title (e.g. the launching task's goal, the PR title). At most <see cref="TitleMaxLength"/> chars.</summary>
    public string Title { get; set; } = default!;

    /// <summary>
    /// What TYPE OF WORK this thread solves — a product semantic, NOT the trigger of any run inside it
    /// (see <see cref="WorkSessionKind"/>). One session holds runs of many different source types.
    /// </summary>
    public WorkSessionKind Kind { get; set; }

    /// <summary>Lifecycle ONLY — never a run status. Live execution state is projected from the runs + decisions.</summary>
    public WorkSessionStatus Status { get; set; } = WorkSessionStatus.Open;

    /// <summary>
    /// Reserved durable thread SCOPE (jsonb) — e.g. the per-repo branch-continuity map + read-only repos.
    /// NULL until the policy/context slices populate it; carried here now so the column lands with the table.
    /// </summary>
    public string? ScopeJson { get; set; }

    /// <summary>
    /// Rolling thread SUMMARY — the LLM-distilled context of OLDER turns (those scrolled out of the recent window),
    /// which a follow-up turn's prompt is primed with ahead of the verbatim recent turns. NULL until a thread grows
    /// past the recent window (a short thread carries no summary — byte-identical to the pre-summary digest).
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Watermark for <see cref="Summary"/>: the highest <c>WorkflowRun.SessionTurnIndex</c> the summary already
    /// covers. The summarizer folds only turns ABOVE this (newly scrolled out of the recent window) into the summary,
    /// then advances it — so distillation is incremental, never a full re-summarize. NULL = no summary yet.
    /// </summary>
    public int? SummaryThroughTurnIndex { get; set; }

    /// <summary>
    /// The highest top-level turn ordinal assigned in this thread — the atomic, race-free turn counter that replaces
    /// the old MAX(SessionTurnIndex)+1 read. Starts at 1 (the opening run's turn, <c>WorkSessionService.FirstTurnIndex</c>).
    /// A CONTINUE atomically increments it (<c>UPDATE … SET last_turn_index = last_turn_index + 1 … RETURNING</c>), so
    /// two concurrent follow-ups to the same session SERIALISE on this row and get DISTINCT ordinals — never a duplicate
    /// turn. A child / replay / sub-workflow run inherits the SessionId with a NULL turn index and never touches this.
    /// </summary>
    public int LastTurnIndex { get; set; } = 1;

    /// <summary>
    /// The thread's most-recent activity instant — the MRU ordering key for the sessions index (newest first). Stamped
    /// at <c>WorkSessionService</c> open (= creation) and bumped on every continue (a new top-level turn). A denormalised
    /// sort key so the index rides <c>(team_id, last_activity_at DESC, id DESC)</c> with no correlated MAX(run) sort.
    /// (Run-completion bumps are a later refinement — open + continue already track the user-perceived activity.)
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Nav to the owning team.</summary>
    public Team Team { get; set; } = default!;
}
