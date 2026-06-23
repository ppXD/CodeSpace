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
    /// Reserved rolling thread SUMMARY — the distilled context a follow-up turn's seed is primed with.
    /// NULL until the context-builder slice populates it; carried here now so the column lands with the table.
    /// </summary>
    public string? Summary { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Nav to the owning team.</summary>
    public Team Team { get; set; } = default!;
}
