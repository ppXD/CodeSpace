using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Plans;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// The per-turn facts the projector gathers from the substrate (the decision tape, the agents' results, the PR node)
/// and hands to the PURE <see cref="RoomNarrative"/>. Keeping the I/O here and the rendering pure means the narrative
/// engine stays unit-testable and the gathering stays one bounded, focused-turn read set (no N+1). All members default
/// empty, so a turn with nothing to say projects exactly as before — <see cref="Empty"/> is the inert baseline.
/// </summary>
public sealed record RoomTurnFacts
{
    /// <summary>The supervisor ROUNDS in tape order — the projector segments the decision tape on each Plan; the narrative renders one Plan / Agents / Operation group PER round (never lumped). Empty for a non-supervisor turn.</summary>
    public IReadOnlyList<RoomRound> Rounds { get; init; } = Array.Empty<RoomRound>();

    /// <summary>The run's durable plan as a live checklist (contract + derived per-item state) — the narrative's plan tracker; when present it REPLACES the per-round plan stat rows. Null when the run persisted no plan (pre-plan runs project exactly as before).</summary>
    public WorkPlanChecklist? Checklist { get; init; }

    /// <summary>The turn's rich final answer (closing text + typed attachments) — emitted last on a terminal turn. Null while in-progress / when there's nothing to deliver.</summary>
    public RoomFinalAnswer? FinalAnswer { get; init; }

    /// <summary>Each running agent's latest PUBLIC activity line, keyed by run id — feeds the live "working…" indicator (never raw CoT). Empty when nothing is running.</summary>
    public IReadOnlyDictionary<Guid, string> LatestLines { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The plan's subtask titles (bounded ≤ 20 by schema) — the flat fallback lead's floor; the render source is <see cref="Rounds"/>.</summary>
    public IReadOnlyList<string> Subtasks { get; init; } = Array.Empty<string>();

    /// <summary>The distinct changed-file paths across the turn's agents — the "Changed N files" row.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The distinct changed files with repository + producing-attempt identity. Empty on legacy facts, where <see cref="ChangedFiles"/> remains authoritative.</summary>
    public IReadOnlyList<RoomFileIdentity> ChangedFileIdentities { get; init; } = Array.Empty<RoomFileIdentity>();

    /// <summary>Files the turn produced as files rather than as a repository change — the only surviving copy once the workspace is gone.</summary>
    public IReadOnlyList<DeliverableFile> Deliverables { get; init; } = Array.Empty<DeliverableFile>();

    /// <summary>+added / −removed line totals across the turn, when captured (a true data-gap today → null → the row omits "+X −Y").</summary>
    public int? Additions { get; init; }
    public int? Deletions { get; init; }

    /// <summary>Total side-effecting tool calls across the turn's agents — the "N tool calls" row. Null when unknown.</summary>
    public int? ToolCalls { get; init; }

    /// <summary>The per-kind tool-call histogram across the turn's agents (e.g. read · edit · test) — the Tools row's detail + items. Empty when unknown.</summary>
    public IReadOnlyList<ToolKindCount> ToolHistogram { get; init; } = Array.Empty<ToolKindCount>();

    /// <summary>Each spawned agent's one-line result summary, keyed by its run id — the agent cards' takeaway and the lead fallback when there's no stop summary. Empty when none produced one.</summary>
    public IReadOnlyDictionary<Guid, string> AgentSummaries { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Each agent's OWN changed-file paths, keyed by its run id (bounded per agent) — the per-agent file attribution the card renders, so a reader sees WHICH agent produced a file rather than the provenance-blind turn-level union. Empty for an agent that changed nothing.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> AgentFiles { get; init; } = new Dictionary<Guid, IReadOnlyList<string>>();

    /// <summary>Each agent's exact changed-file identities. Multi-repo equal paths remain separate; empty on legacy facts.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<RoomFileIdentity>> AgentFileIdentities { get; init; } = new Dictionary<Guid, IReadOnlyList<RoomFileIdentity>>();

    /// <summary>What each agent left behind that nobody has reclaimed yet, keyed by its run id — folded from the run's typed cleanup receipts. Absent for an agent with nothing outstanding, which is nearly all of them.</summary>
    public IReadOnlyDictionary<Guid, RoomRunRecovery> AgentRecovery { get; init; } = new Dictionary<Guid, RoomRunRecovery>();

    /// <summary>Each agent's durable log-stream health. An absent agent has no declared/captured stream and remains unsaid; a present summary never changes the task verdict.</summary>
    public IReadOnlyDictionary<Guid, RoomAgentLogSummary> AgentLogs { get; init; } = new Dictionary<Guid, RoomAgentLogSummary>();

    /// <summary>The run's priced spend, token usage, cap, and durable budget commitments. Null when the run has no cost or budget evidence.</summary>
    public RoomBudgetSummary? Budget { get; init; }

    /// <summary>How many reasoning entries the turn produced — the "Reasoning" row's count.</summary>
    public int ReasoningCount { get; init; }

    /// <summary>The turn's reasoning step texts (bounded), surfaced when the Reasoning row is expanded. Empty when none produced.</summary>
    public IReadOnlyList<string> ReasoningSteps { get; init; } = Array.Empty<string>();

    /// <summary>The objective acceptance verdict (the Review stage): true = passed, false = failed, null = not graded.</summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>Every repository delivery outcome in the current set. Failures and skips remain alongside opened PRs.</summary>
    public IReadOnlyList<RoomDelivery> Deliveries { get; init; } = Array.Empty<RoomDelivery>();

    /// <summary>
    /// True when the completion authority REFUSED this attempt's terminal and parked it. The reason itself arrives as
    /// the run's own error, so this is only the discriminator the narrative needs between the two Suspended shapes:
    /// a completion park (this — refused, resumable only by an operator) and an ask-park (waiting on its own signal).
    /// </summary>
    public bool CompletionParked { get; init; }

    /// <summary>The raw engine error, surfaced behind "Show raw error" on a failure diagnostic. Null when there's nothing rawer than the humanized text.</summary>
    public string? RawError { get; init; }

    /// <summary>The completion stage this run's repository policy put out of reach, in the BACKEND's own words (<c>UpstreamStageTrace.NotApplicableIntegration</c>) — a patch-only run finishes with nothing integrated BY POLICY, and without this line the Room shows an operator a clean Success and no account of where the branch went. Null (the overwhelming case) whenever every stage was genuinely owed.</summary>
    public string? PolicyBoundedStage { get; init; }

    /// <summary>The run's EFFECTIVE network posture as one line (<c>AgentAutonomyPolicy.DescribeNetwork</c>) — whether these agents had the internet, and whether that was the launcher's choice, the route's ceiling, or this deployment's own. Null when nothing can say it: a run with no route provenance (an authored workflow run, or a task run staged before the launch stamped its resolved tier) speaks only when the deployment ceiling clamped it — never guessed.</summary>
    public string? NetworkPosture { get; init; }

    /// <summary>The supervisor's RETRY beats in tape order — one per retry decision ("Supervisor retried a subtask"). The narrative renders each as a step so the room shows the recovery flow, not just the surviving agents. Empty when the turn had no retries.</summary>
    public IReadOnlyList<RoomRetryStep> RetrySteps { get; init; } = Array.Empty<RoomRetryStep>();

    /// <summary>The supervisor's RE-SPAWN waves in tape order — each additional Spawn decision that re-dispatched an already-spawned subtask (a second/third wave). The authored phase group anchors only each subtask's FIRST attempt, so a later wave (and its failed agent) is otherwise dropped; the narrative renders each as its own chronological wave, mirroring <see cref="RetrySteps"/>. Empty when every subtask was spawned once.</summary>
    public IReadOnlyList<RoomRespawnStep> RespawnSteps { get; init; } = Array.Empty<RoomRespawnStep>();

    /// <summary>P22-9b — the per-unit quality recommendation the run's newest decision froze onto its row. Read straight off the durable column, never recomputed here, so the Room shows what the turn was actually shown. Empty for every run written before the column existed and for a run no unit was attempted in.</summary>
    public IReadOnlyList<RoomQualityRecommendation> QualityRecommendations { get; init; } = Array.Empty<RoomQualityRecommendation>();

    public static readonly RoomTurnFacts Empty = new();
}

/// <summary>One unit's recorded quality recommendation, flattened for rendering (the mechanism as its name — the Room renders strings, never enums). A recommendation the brain was free to reject, which is why the reason travels with it.</summary>
public sealed record RoomQualityRecommendation(string SubtaskId, string Mechanism, string Reason);

/// <summary>One supervisor RETRY beat — the tape sequence it landed at and the fresh agent it staged (null for a no-op retry). Rendered as that agent's own "Retry" card so the recovery reads chronologically; the retry's line + rationale live on the Journal ③ beat now, not the room.</summary>
public sealed record RoomRetryStep(long Sequence, Guid? AgentRunId);

/// <summary>One supervisor RE-SPAWN wave — the tape sequence the additional Spawn decision landed at and the agent-run ids it staged for already-spawned subtasks (wave ≥ 2). Rendered as a narrative step ("Supervisor spawned N agents again") + that wave's agent cards, so the room shows EVERY spawn wave the run really ran, not just the first. Empty <see cref="AgentRunIds"/> can't occur (a wave with no re-spawned agent isn't emitted).</summary>
public sealed record RoomRespawnStep(long Sequence, IReadOnlyList<Guid> AgentRunIds);

/// <summary>One bucket of the tool-call histogram — a tool kind and how many times the turn's agents called it.</summary>
public sealed record ToolKindCount(string Kind, int Count);

/// <summary>
/// One supervisor ROUND — a Plan decision and every decision up to the next Plan, in tape order. The projector segments
/// the raw decision tape on <c>Kind == Plan</c> (a re-plan opens a new round); it renders one Plan-stat + this round's
/// agent group per round.
/// </summary>
public sealed record RoomRound
{
    /// <summary>1-based round number.</summary>
    public required int Index { get; init; }

    /// <summary>THIS round's plan subtask titles (not the whole run's) — the "Plan · N subtasks" row.</summary>
    public IReadOnlyList<string> Subtasks { get; init; } = Array.Empty<string>();

    /// <summary>This round's spawned / retried / resolved agent run ids, in tape (staging) order.</summary>
    public IReadOnlyList<Guid> AgentRunIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>The turn's rich final result — the closing text plus typed attachments (files / PR / images).</summary>
public sealed record RoomFinalAnswer
{
    public string? Text { get; init; }
    public IReadOnlyList<RoomAttachment> Attachments { get; init; } = Array.Empty<RoomAttachment>();

    /// <summary>True when the run STOPPED on a graceful FAILURE (a fail-closed no-decision / no-model / unknown-decision outcome — no task work delivered) rather than a genuine success. The card renders neutral/degraded, not a green "Result", so a supervisor that gave up doesn't read as done.</summary>
    public bool Degraded { get; init; }

    /// <summary>Why the card is degraded, when <see cref="Text"/> does NOT already say so — a give-up / forced stop's text IS its own account, but a FAILED objective acceptance grade leaves the model's success-sounding closing line untouched, so the ledger's verdict is stated here. Backend-authored copy. Null whenever the text already accounts for the degrade (and always on a clean success).</summary>
    public string? DegradedReason { get; init; }

    /// <summary>C1 — whether ANY check examined this result (an acceptance grade, or an output-critic verdict). False on a clean Success that carries neither; null when the question does not arise (a degraded stop already states its own account).</summary>
    public bool? Verified { get; init; }

    /// <summary>The unverified chip's backend-authored copy. Null whenever <see cref="Verified"/> is not false.</summary>
    public string? VerificationNote { get; init; }
}

/// <summary>One typed final-answer attachment (a file, the PR, or an image).</summary>
public sealed record RoomAttachment(AnswerAttachmentKind Kind, string Label, string? Url, string? PreviewUrl, string? DownloadUrl, RoomFileIdentity? File = null);

/// <summary>The PR / change set a turn produced — joined from the run's open-PR node. Provider-agnostic.</summary>
public sealed record RoomDelivery
{
    public required string Title { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? RepositoryAlias { get; init; }
    public RoomPullRequestDisposition? Disposition { get; init; }
    public string? Reference { get; init; }
    public string? BranchHead { get; init; }
    public string? BranchBase { get; init; }
    public string? Checks { get; init; }
    public bool? ChecksOk { get; init; }
    public string? Url { get; init; }
    public string? Error { get; init; }

    /// <summary>THIS repository's own per-check verification truth (P21) — see <see cref="RoomArtifactVerification"/>.</summary>
    public IReadOnlyList<RoomArtifactVerification> Verifications { get; init; } = Array.Empty<RoomArtifactVerification>();
}

/// <summary>Turn-facing reduction of one agent's durable log streams. Detail is backend-authored from persisted stream state and integrity evidence.</summary>
public sealed record RoomAgentLogSummary(RoomAgentLogStatus Status, int StreamCount, string Detail);

public enum RoomAgentLogStatus
{
    Verified,
    Captured,
    Finalizing,
    Incomplete,

    /// <summary>
    /// Open, and its remote storage is refusing the segments it is holding. Distinct from <see cref="Finalizing"/>
    /// because nothing is progressing and nothing is lost either — the bytes are queued behind an outage, which is the
    /// one fact an operator can act on. Appended rather than slotted into the severity order (which
    /// <c>RoomNarrative.LogStatusRank</c> owns explicitly) so no existing member's ordinal moves: nothing persists one
    /// today, and a value shifted underneath a stored ordinal is unrecoverable.
    /// </summary>
    Stalled,
}

/// <summary>Run-level budget truth. Estimated spend and committed reservation headroom remain separate because an unresolved commitment is not an actual bill.</summary>
public sealed record RoomBudgetSummary
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal? AgentExecutionUsd { get; init; }
    public decimal? BrainPlaneUsd { get; init; }
    public decimal? TotalUsd { get; init; }
    public int UnknownAgentRuns { get; init; }
    public int UnknownBrainCalls { get; init; }
    public decimal? CommittedUsd { get; init; }
    public decimal? CapUsd { get; init; }
    public int UnresolvedClaims { get; init; }
    /// <summary>Spend recorded under an <c>unbudgeted:</c> ledger kind — a plane with no run-level cap. Surfaced on its own because it never counts toward <see cref="CommittedUsd"/> or <see cref="CapUsd"/>.</summary>
    public decimal? UnbudgetedUsd { get; init; }

    /// <summary>
    /// P15-5b-ii: the TEAM's standing cap this run was also admitted against, and what the team has committed
    /// inside its window. Null when the team has no cap and the deployment sets no fallback — so an unset cap stays
    /// unsaid rather than rendering as an unlimited one. Team-wide figures, deliberately kept apart from
    /// <see cref="CapUsd"/> / <see cref="CommittedUsd"/>, which are this run's own.
    /// </summary>
    public decimal? TeamCapUsd { get; init; }

    public decimal? TeamCommittedUsd { get; init; }

    /// <summary>Which cap <see cref="TeamCapUsd"/> came from — the team's own row or the deployment fallback. Null with the cap.</summary>
    public BudgetCapGrain? TeamCapGrain { get; init; }

    /// <summary>The window <see cref="TeamCommittedUsd"/> is summed over (e.g. <c>rolling-30d</c>). Null with the cap.</summary>
    public string? TeamCapWindow { get; init; }
}
