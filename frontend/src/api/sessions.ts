import { ApiError, fetchJson } from "./request";
import type { WorkflowRunStatus } from "./workflows";

// ─── Types (mirror backend DTOs in CodeSpace.Messages.Dtos.Sessions) ───

/// Lifecycle of a work thread. Mirrors backend `WorkSessionStatus`.
export type WorkSessionStatus = "Open" | "Archived";

/// What type of work a thread solves. Mirrors backend `WorkSessionKind` — an open string for forward-compatibility
/// with new kinds (Task / Pr / Issue / Workflow / Schedule / Custom today).
export type WorkSessionKind = string;

/// One row of the sessions index. Mirrors backend `SessionSummary`.
export interface SessionSummary {
  id: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  turnCount: number;
  createdDate: string;
  lastActivityAt: string;
  latestRunId?: string | null;
  latestRunStatus?: WorkflowRunStatus | null;
  latestProjectionKind?: string | null;
  hasPendingDecision: boolean;
}

/// One keyset page of the sessions index. Mirrors backend `SessionPage`.
export interface SessionPage {
  items: SessionSummary[];
  nextCursor?: string | null;
}

/// One repo's produced branch within a multi-repo turn. Mirrors backend `SessionTurnRepoResult`.
export interface SessionTurnRepoResult {
  repositoryId: string;
  producedBranch: string;
}

/// One attempt (a replay/rerun fork) of a turn. Mirrors backend `SessionTurnAttempt`.
export interface SessionTurnAttempt {
  runId: string;
  attemptNumber: number;
  status: WorkflowRunStatus;
  sourceType: string;
  rerunFromNodeId?: string | null;
  createdDate: string;
  isLatest: boolean;
}

/// One turn of a thread (a top-level run shown like a chat exchange). Mirrors backend `SessionTurn`.
export interface SessionTurn {
  turnIndex: number;
  turnRunId: string;
  runId: string;
  userMessage?: string | null;
  runStatus: WorkflowRunStatus;
  projectionKind?: string | null;
  result?: string | null;
  producedBranch?: string | null;
  repositoryResults?: SessionTurnRepoResult[] | null;
  hasPendingDecision: boolean;
  createdDate: string;
  startedAt?: string | null;
  completedAt?: string | null;
  error?: string | null;
  attemptCount: number;
  attempts?: SessionTurnAttempt[] | null;
}

/// One thread as a conversation. Mirrors backend `SessionDetail`.
export interface SessionDetail {
  id: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  createdDate: string;
  summary?: string | null;
  summaryThroughTurnIndex?: number | null;
  /// When entered by a run id, the turn that run belongs to (the UI scrolls to it). Null when entered by session id.
  anchorTurnIndex?: number | null;
  turns: SessionTurn[];
}

export type SessionRunMetadataSelector =
  | { kind: "Session"; sessionId: string; runAnchorId: null }
  | { kind: "RunAnchor"; sessionId: null; runAnchorId: string };
export type SessionRunMetadataPageDirection = "Tail" | "Older";
export type SessionRunMetadataTextState = "None" | "Complete" | "Truncated" | "Corrupt";
export type WorkflowRunRequestStatus = "Received" | "Verified" | "Normalized" | "Matched" | "Consumed" | "Rejected";

export interface SessionRunMetadataPageRequest {
  direction: SessionRunMetadataPageDirection;
  cursor?: string;
  /** Required for Older so the client can reject a continuation that changes membership family. Never sent separately. */
  membershipHeadRunNumber?: number;
  limit: number;
}

export interface SessionRunMetadataText {
  text: string | null;
  sizeBytes: number;
  state: SessionRunMetadataTextState;
}

export interface SessionRunMetadataItem {
  runId: string;
  runNumber: number;
  runRequestId: string;
  rootRunId: string | null;
  sessionTurnIndex: number | null;
  status: WorkflowRunStatus;
  projectionKind: SessionRunMetadataText;
  sourceType: SessionRunMetadataText;
  rerunFromNodeId: SessionRunMetadataText;
  createdDate: string;
  startedAt: string | null;
  completedAt: string | null;
  error: SessionRunMetadataText;
  requestStatus: WorkflowRunRequestStatus;
  requestReceivedAt: string;
}

/** MembershipHeadRunNumber freezes membership only. Mutable status/error/timing are fresh per-page observations. */
export interface SessionRunMetadataPage {
  selector: SessionRunMetadataSelector;
  sessionId: string;
  direction: SessionRunMetadataPageDirection;
  requestCursor: string | null;
  limit: number;
  membershipHeadRunNumber: number;
  anchorRootRunId: string | null;
  consistency: "MembershipHeadOnly";
  items: SessionRunMetadataItem[];
  omitted: { older: boolean; newer: boolean };
  continuation: { olderCursor: string | null; returnToTail: boolean };
}

export class InvalidSessionRunMetadataPageError extends Error {
  constructor() {
    super("Invalid Session run metadata page response.");
    this.name = "InvalidSessionRunMetadataPageError";
  }
}

// ─── Session Room (the backend-authored AI work transcript) ───
// Mirrors backend CodeSpace.Messages.Dtos.Sessions.Room. The frontend renders blocks by `type` and owns no
// copy / order / status — an UNKNOWN `type` falls to a generic renderer, so a new backend block kind needs zero
// frontend change (forward-compatible, matching the backend's additive [JsonDerivedType]).

export type ExecutionStepStatus = "Pending" | "Queued" | "Running" | "Done" | "Failed" | "Blocked" | "Skipped";
export type NarrativeTone = "Info" | "Success" | "Error";
export type RoomActionKind = "RerunTurn" | "RerunFromNode" | "RerunFailedMapItems" | "RetryFailedAgent" | "AnswerDecision" | "Stop" | "Continue" | "OpenTrace" | "FixCredentials" | "OpenPullRequest";

/// A capability-aware action on a turn. `enabled` + `disabledReason` come from the backend, so a click never 422s.
export interface RoomAction {
  kind: RoomActionKind;
  label: string;
  enabled: boolean;
  disabledReason?: string | null;
  target?: string | null;
  attempt?: boolean;
  /** `OpenPullRequest` only: set once a PR already exists for this run — render a "View PR" link instead of a button. */
  url?: string | null;
}

export type RoomPullRequestDisposition = "Opened" | "AlreadyOpened" | "Skipped" | "Failed";

/** One repository's PR-open outcome (PR-6) — a multi-repo run's per-repo failure never sinks the whole set. */
export interface RoomPullRequestOpened {
  repositoryId?: string | null;
  alias: string;
  disposition: RoomPullRequestDisposition;
  number?: number | null;
  url?: string | null;
  error?: string | null;
}

export interface RoomPullRequestResult {
  pullRequests: RoomPullRequestOpened[];
}

export interface ExecutionMapStep {
  id: string;
  label: string;
  status: ExecutionStepStatus;
  /// A short per-step detail under the label — "8s" / "3 agents" / "passed" / "not verified" (succeeded but no
  /// oracle ran) / "1 of 2" / "skipped". Null when none.
  detail?: string | null;
}

export interface StatItem {
  text: string;
  file?: RoomFileIdentity | null;
  detail?: string | null;
  tone?: NarrativeTone | null;
}

/** Stable identity of one changed file; repo-relative path alone is ambiguous in a multi-repository run. */
export interface RoomFileIdentity {
  path: string;
  agentRunId?: string | null;
  repositoryId?: string | null;
  repositoryAlias?: string | null;
}

/// One agent in a group — `agentRunId` is enough to drive the live terminal (it self-polls).
export interface RoomAgentCard {
  agentRunId: string;
  label: string;
  role?: string | null;
  /// The planned subtask this agent was assigned (the model's decomposition). Null for a non-supervisor / homogeneous spawn.
  assignedSubtask?: string | null;
  status: string;
  /// The (already secret-redacted) failure cause for a NON-succeeded agent — the real reason (e.g. an LLM 4xx) so the card names WHY it failed. Null on success. On a journal card it's carried from the backend; on a room card it's a display-only field the journal→room adapter fills.
  error?: string | null;
  model?: string | null;
  /// The harness the agent ran on (e.g. "codex-cli" / "claude-code") — the small harness glyph on the card. Null when unknown.
  harness?: string | null;
  tokens?: number | null;
  costUsd?: number | null;
  filesChanged?: number | null;
  /// This agent's OWN changed-file paths (bounded) — per-agent attribution so the UI shows WHICH agent produced a file
  /// (open the agent → preview its exact version). Empty for an agent that changed nothing.
  changedFiles?: string[] | null;
  /// Exact repository + producing-attempt identities behind `changedFiles`.
  changedFileIdentities?: RoomFileIdentity[] | null;
  /// Tool calls the agent made — for the card meta "3 files · 6 tool calls · 41s". Null when unknown.
  toolCount?: number | null;
  /// Wall-clock for the agent — final once terminal, else live elapsed. Null before it starts.
  durationMs?: number | null;
  /// The agent's own one-line result takeaway (what it concluded) — shown on the card before any raw log.
  summary?: string | null;
  latestLine?: string | null;
  /// The workflow node + iteration this agent ran as (the cell key) — lets the opened terminal fetch this cell's attempt
  /// history and switch between attempts, like Activity. Null for a supervisor-spawned agent (no workflow cell to switch).
  nodeId?: string | null;
  iterationKey?: string | null;
  /// Whether this agent CONTINUED a prior conversation (a retry resumed the earlier session) — the "⟳ resumed" chip.
  resumed?: boolean;
  /// The LATEST independent reviewer's verdict on this agent's output — the "✓ reviewed" / "⚠ flagged" chip.
  review?: JournalReviewVerdict | null;
  /// Whether the agent's self-report disagreed with its objective acceptance check ("over_claim" / "under_claim").
  /// On a journal card it comes from the backend; on a room card the journal→room adapter fills it, like `error`.
  contradiction?: string | null;
}

export interface RoomDecisionOption {
  id: string;
  label: string;
  sideEffecting?: boolean;
}

interface RoomBlockBase {
  id: string;
  seq: number;
}

export interface UserMessageBlock extends RoomBlockBase { type: "user_message"; text: string; at?: string | null; }
export interface ExecutionMapBlock extends RoomBlockBase { type: "execution_map"; steps: ExecutionMapStep[]; }
export interface AgentGroupBlock extends RoomBlockBase { type: "agent_group"; title: string; agents: RoomAgentCard[]; }
/// A collapsible stat row — one generic block for subtasks / files / tools / reasoning (the projector fills kind/label/detail/items).
export interface StatBlock extends RoomBlockBase { type: "stat"; kind: string; label: string; detail?: string | null; items?: StatItem[] | null; }
/// One checkable line of the plan checklist — the item's contract plus its tape-derived execution state.
export interface PlanChecklistItem {
  ordinal: number;
  itemId: string;
  title: string;
  kind?: string | null;
  /// WorkPlanItemStates value (open vocabulary): Pending / InProgress / Completed / Failed / NeedsReview.
  state: string;
  /// 1-based ordinals of the items this one depends on — rendered "after #1, #3".
  dependsOn?: number[] | null;
  acceptanceLabel?: string | null;
  /// "TestsPass" (command chip) or "ArtifactPresent" (deliverable chip) — picks the chip icon.
  acceptanceKind?: string | null;
  acceptancePassed?: boolean | null;
  acceptanceDetail?: string | null;
  acceptanceCriteria?: string[] | null;
  agentRunId?: string | null;
  attempts: number;
}
export interface RoomPlanQuestionOption { id: string; label: string; recommended?: boolean; }
/// A planner-authored operator question — interactive while the plan awaits confirmation, read-only after.
export interface RoomPlanQuestion { id: string; question: string; options?: RoomPlanQuestionOption[] | null; allowFreeText?: boolean; }
/// The run's durable plan as a live checklist — the whole current version with per-item execution state.
export interface PlanChecklistBlock extends RoomBlockBase {
  type: "plan_checklist";
  label: string;
  version: number;
  status: string;
  detail?: string | null;
  items: PlanChecklistItem[];
  assumptions?: string[] | null;
  questions?: RoomPlanQuestion[] | null;
  hasPriorVersions?: boolean;
}
/// The outcome of answering a pending plan-confirmation card (S3 gate). `resumed` is false when a concurrent
/// answer won the wait first (first answer wins).
export interface WorkPlanConfirmationOutcome { resumed: boolean; approved: boolean; }

/// The structured verdict a human-in-the-loop supervisor card is ruled on — the wire values the backend's
/// `SupervisorAnswerDecision` declares. Sent explicitly by every gate surface so the verdict is a field, not a
/// word the server has to find at the front of whatever language the operator typed.
export type SupervisorAnswerDecision = "approve" | "revise" | "reject";
/// The delivered change set (PR card).
export interface DeliveryBlock extends RoomBlockBase {
  type: "delivery";
  title: string;
  reference?: string | null;
  branchHead?: string | null;
  branchBase?: string | null;
  checks?: string | null;
  checksOk?: boolean | null;
  url?: string | null;
}
/// One file a turn produced as a file. `artifactId` is what fetches its bytes.
export interface DeliverableFile {
  path: string;
  kind: string;
  sizeBytes: number;
  contentType: string;
  artifactId: string;
  agentRunId: string;
}
/// Files a turn produced as files rather than as a repository change. Absent when it produced none — an empty
/// list would read as "it produced nothing", which is a claim about the run rather than about this card.
export interface DeliverablesBlock extends RoomBlockBase {
  type: "deliverables";
  title: string;
  files: DeliverableFile[];
}
export interface DiagnosticBlock extends RoomBlockBase {
  type: "diagnostic";
  tone?: NarrativeTone;
  title?: string | null;
  text: string;
  actions?: RoomAction[] | null;
  rawDetail?: string | null;
}
export interface DecisionBlock extends RoomBlockBase {
  type: "decision";
  decisionId: string;
  question: string;
  shape: string;
  options?: RoomDecisionOption[] | null;
  risk?: string | null;
  deadline?: string | null;
}
export type AnswerAttachmentKind = "Image" | "FileLink" | "Pr";
/// One typed attachment of the final answer — an image, a file link, or the PR.
export interface AnswerAttachment {
  kind: AnswerAttachmentKind;
  label: string;
  url?: string | null;
  previewUrl?: string | null;
  downloadUrl?: string | null;
  /// For a file: the run id of the agent that PRODUCED it — the preview opens THAT agent's exact version.
  agentRunId?: string | null;
  /// For a file: a short label of the producing agent (its role / subtask) — the "· from <agent>" provenance cue.
  producer?: string | null;
  /// Exact repository + producing-attempt identity for a file attachment.
  file?: RoomFileIdentity | null;
}
/// The turn's rich final result — closing text + typed attachments (files / PR / images), rendered distinctly.
export interface FinalAnswerBlock extends RoomBlockBase {
  type: "final_answer";
  text?: string | null;
  attachments?: AnswerAttachment[] | null;
  degraded?: boolean;
  /// Backend-authored account of WHY the card is degraded, when `text` doesn't already carry it (a failed acceptance
  /// check leaves the model's own success-sounding line intact). Rendered verbatim as the card's heading.
  degradedReason?: string | null;
}
/// A live "working…" line pinned at the bottom of an active turn (latest public activity, never raw CoT).
export interface LiveActivityBlock extends RoomBlockBase {
  type: "live_activity";
  text: string;
  agentRunId?: string | null;
}
export interface AssistantTurnBlock extends RoomBlockBase {
  type: "assistant_turn";
  turnIndex: number;
  turnRunId: string;
  runId: string;
  status: WorkflowRunStatus;
  summary?: string | null;
  map?: ExecutionMapBlock | null;
  blocks: RoomBlock[];
  actions: RoomAction[];
  at?: string | null;
  /// Wall-clock so far — final once terminal, else live elapsed. Null before it starts.
  durationMs?: number | null;
  /// The turn's rerun/replay attempts (oldest → newest) — the header's "N attempts" timeline. Empty for a never-rerun turn.
  attempts?: RoomTurnAttempt[];
}

/// One attempt of a turn (the original + each rerun/replay fork). Mirrors backend `RoomTurnAttempt`.
export interface RoomTurnAttempt {
  runId: string;
  attemptNumber: number;
  status: WorkflowRunStatus;
  at: string;
  /// The attempt the turn currently shows (the newest) — rendered as "shown", not an open link.
  isCurrent: boolean;
}

export type RoomBlock =
  | UserMessageBlock
  | AssistantTurnBlock
  | ExecutionMapBlock
  | AgentGroupBlock
  | StatBlock
  | PlanChecklistBlock
  | DeliveryBlock
  | DeliverablesBlock
  | DecisionBlock
  | DiagnosticBlock
  | FinalAnswerBlock
  | LiveActivityBlock;

/// A generic preview of one file a turn produced — resolved backend-side from the producing agent's captured diff.
/// The frontend renders by `kind`: `text` (full content), `diff` (unified-diff section), `binary` / `unavailable`
/// (a notice + optional source link). Mirrors backend `RoomFilePreview`.
export interface RoomFilePreview {
  path: string;
  identity?: RoomFileIdentity | null;
  kind: "text" | "diff" | "binary" | "unavailable";
  changeKind?: string | null;
  text?: string | null;
  sizeBytes?: number | null;
  truncated: boolean;
  sourceUrl?: string | null;
  note?: string | null;
  unavailableReason?: "NotInChangeSet" | "AmbiguousRepository" | "MetadataMissing" | "PhysicalObjectMissing" | "IntegrityFailure" | "BackendUnavailable" | "AccessDenied" | "ReconstructionUnavailable" | null;
}

/// One session as a backend-authored transcript. Mirrors backend `RoomView`.
export interface RoomView {
  sessionId: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  cursor: number;
  anchorBlockId?: string | null;
  blocks: RoomBlock[];
}

// ═══ Session Journal — the chronological work transcript (the new /journal surface, built alongside the room) ═══

/// A journal step's render tone — the timeline's closed severity axis. Mirrors backend `TimelineSeverity`.
export type JournalTone = "Info" | "Success" | "Warning" | "Error";

/// One changed file with its +added / −removed line counts (git ground truth; a binary file's counts are null). Mirrors backend `FileDiffStat`.
export interface JournalFileStat {
  path: string;
  additions?: number | null;
  deletions?: number | null;
}

/// One agent a supervisor decision spawned / re-ran — the card the journal hangs off a spawn step. Mirrors backend `JournalAgentCard`.
export interface JournalAgentCard {
  agentRunId: string;
  label: string;
  /// The human-readable planned subtask title — shown on hover over the (slug) label + in the drawer strip, so the
  /// readable title isn't lost when the header is the id. Null for a non-supervisor / homogeneous agent.
  assignedSubtask?: string | null;
  status: string;
  /// The (already secret-redacted) failure cause for a NON-succeeded agent — the real reason (e.g. an LLM 4xx like "Unexpected message role") so the card names WHY it failed. Null on a succeeded card.
  error?: string | null;
  model?: string | null;
  /// The harness the agent ran on (e.g. "codex-cli" / "claude-code") — the small harness glyph on the card. Null when unknown.
  harness?: string | null;
  durationMs?: number | null;
  tokens?: number | null;
  toolCount?: number | null;
  costUsd?: number | null;
  filesChanged?: number | null;
  files: JournalFileStat[];
  resumed: boolean;
  /// The LATEST independent reviewer's verdict on this agent's produced work — the "✓ reviewed" / "⚠ flagged" chip
  /// + the reviewer-run deep-link. Null when the output was never agent-reviewed (or the review hasn't landed).
  review?: JournalReviewVerdict | null;
  /// Whether this agent's own self-report CONTRADICTED its objective acceptance check — an `AgentContradiction` kind
  /// ("over_claim" / "under_claim"), from a supervisor unit's folded compact or the run's own graded result. Null
  /// when the run carried no oracle, the claim and the check agreed, or no verdict was minted.
  contradiction?: string | null;
}

/// A reviewer's VERDICT — a real reviewer agent run's conclusion, or an in-process MODEL critic's — rides a REVIEW
/// step and the reviewed producer's card. Mirrors backend `JournalReviewVerdict`.
export interface JournalReviewVerdict {
  approved: boolean;
  rationale: string;
  /// Evidence-attached issues, each pre-rendered "text (evidence: …)". Empty on an approval.
  issues: string[];
  /// The reviewer's own agent run — deep-linked as "view reviewer run →". NULL for a model critic (no run to open).
  reviewerRunId?: string | null;
  reviewerHarness?: string | null;
  /// The MODEL a model critic ran on — names the reviewer instead of "a second AI". Null for an agent reviewer / a pre-existing verdict.
  reviewerModel?: string | null;
  /// True when the reviewer ran on the PRODUCER's own model — an independently prompted call, but NOT a second opinion, so the card must not say "independent". Decided by the backend, which holds both models.
  sameModelAsProducer?: boolean;
  /// What was reviewed — "output" / "plan" / "decision".
  scope: string;
}

/// A planned subtask still blocked by an unmet dependency at a wave (the "waiting on #n"). Mirrors backend `JournalDeferredSubtask`.
export interface JournalDeferredSubtask {
  subtaskId: string;
  waitingOn: string[];
}

/// One planned subtask on a PLAN step — the model's authored plan, rendered inline under "planned the work". Mirrors backend `JournalSubtask`.
export interface JournalSubtask {
  subtaskId: string;
  title: string;
}

/** A typed bounded-read gap on one durable supervisor Plan decision. Present only when observation was incomplete. */
export interface JournalObservationCoverage {
  sourceKind: string;
  reason: string;
  observedCount: number;
  omittedCount: number;
  omittedCountIsLowerBound: boolean;
  decisionId: string;
  /** Exact Int64 decimal identity; intentionally not a JavaScript number. */
  storyOrder: string;
}

/// One chronological step of a run's work journal — the frontend renders by `kind`. Mirrors backend `JournalStep`.
/// The structured facts of one model call — mirrors backend `JournalModelCall`. Rendered as a row in the expanded model
/// fold (purpose · model · tokens · latency · cost · status). Cost/latency/tokens are null when unknown (unpriced model,
/// unpaired start, usage-silent call).
export interface JournalModelCall {
  purpose: string;
  model?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  tokens?: number | null;
  latencyMs?: number | null;
  costUsd?: number | null;
  status: string;
  error?: string | null;
  /// A caution on an otherwise-completed call — "output truncated" / "content filtered" when the provider cut the answer
  /// off (a length cap / policy block). Null on a clean completion and on a failed call (its `error` carries the reason).
  finishNote?: string | null;
}

/// The full, on-demand detail of one model call — mirrors backend `ModelCallDetail`. Fetched when the drawer opens; each
/// field is a display string (offloaded prompt/result resolved to text), null when the call didn't carry it.
export interface ModelCallDetail {
  prompt?: string | null;
  result?: string | null;
  usage?: string | null;
  trace: string;
}

export interface JournalStep {
  id: string;
  cursor: string;
  at: string;
  kind: string;
  /// Whether this step is an orchestration BEAT — a curated milestone shown in the ③ timeline (a supervisor decision, a map/planner node's dispatch, …). Non-beats fold into "background steps". Generic across run shapes.
  beat: boolean;
  /// For a beat step, its semantic verb (plan / spawn / retry / ask_human / merge / resolve / stop / dispatch) — the semantic pill. Null for a non-beat step.
  verb?: string | null;
  title: string;
  detail?: string | null;
  rationale?: string | null;
  /// The operator's answer on an ASK_HUMAN step (approve, or the requested change) — a structured field the FE renders
  /// as its own "└ answer" line rather than parsing it out of the joined question detail. Null unless answered.
  answer?: string | null;
  /// The structured facts of a MODEL_CALL step (purpose · model · tokens · latency · cost · status) — the expanded model
  /// fold renders these as a legible row. Null on every non-model-call step.
  modelCall?: JournalModelCall | null;
  /// The independent reviewer's verdict on a REVIEW step — rendered as the verdict card under the beat. Null on every non-review step.
  review?: JournalReviewVerdict | null;
  /// Whether an ASK step is a review-gate ESCALATION (the hard-Gate ladder parked the run on the human) — the "review-blocked" framing chip.
  reviewEscalation?: boolean;
  /// The DISCARDED DRAFT this decision replaced ("plan draft · via metis-coder-max · 8.2k tokens") — the "└ replaced a draft"
  /// line under a decision that went through a critic revision, attributing the once-anonymous authoring call. Null otherwise.
  draft?: string | null;
  /// Whether an ASK step is the PLAN-CONFIRMATION card — the plan checklist card is that park's answer surface, so the generic inline answer bar is suppressed.
  planConfirmation?: boolean;
  tone: JournalTone;
  milestone: boolean;
  agents: JournalAgentCard[];
  deferred: JournalDeferredSubtask[];
  /// The subtasks this PLAN step authored — rendered inline under "planned the work". Empty for a non-plan step.
  plan: JournalSubtask[];
  /** Explicit unavailable/omitted Plan observation facts. Absent on the healthy wire. */
  observationCoverage?: JournalObservationCoverage[] | null;
  agentRunId?: string | null;
  nodeId?: string | null;
}

/// One attempt of a turn — a rerun / replay of the same user message. Mirrors backend `JournalAttempt`.
export interface JournalAttempt {
  attemptNumber: number;
  runId: string;
  status: WorkflowRunStatus;
  at: string;
  sourceType: string;
  rerunFromNodeId?: string | null;
  isLatest: boolean;
  focused: boolean;
  error?: string | null;
}

/// One turn of the journal — a user message + the AI's reply as chronological steps. Mirrors backend `JournalTurn`.
export interface JournalTurn {
  turnIndex: number;
  turnRunId: string;
  runId: string;
  status: WorkflowRunStatus;
  userMessage?: string | null;
  summary?: string | null;
  at?: string | null;
  durationMs?: number | null;
  focused: boolean;
  steps: JournalStep[];
  stepCount: number;
  attempts: JournalAttempt[];
}

/// A session as a chronological work journal. Mirrors backend `JournalView`.
export interface JournalView {
  sessionId: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  cursor: string;
  anchorTurnIndex?: number | null;
  turns: JournalTurn[];
}

// ─── Client (mirrors src/api/workflows.ts — fetchJson, auto JWT + X-Team-Id) ───

/** Build the exact file-preview URL. Optional coordinates preserve legacy path-only callers while exact rows stay repo-bound. */
export function roomFileUrl(runId: string, file: RoomFileIdentity): string {
  const query = new URLSearchParams({ path: file.path });
  if (file.agentRunId) query.set("agentRunId", file.agentRunId);
  if (file.repositoryId) query.set("repositoryId", file.repositoryId);
  if (file.repositoryAlias) query.set("repositoryAlias", file.repositoryAlias);
  return `/api/sessions/by-run/${runId}/room/file?${query.toString()}`;
}

export const sessionsApi = {
  /// The team's sessions, most-recently-active first, keyset-paginated.
  listTeamSessions: (cursor?: string, limit = 30) =>
    fetchJson<SessionPage>(`/api/sessions?limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`),

  /// One thread as a conversation (turns + nested attempts).
  getSessionDetail: (sessionId: string) => fetchJson<SessionDetail>(`/api/sessions/${sessionId}`),

  /** A hard-bounded, exact-identity membership window. It is intentionally not a React Query whole-history feed. */
  pageRunMetadata: async (selector: SessionRunMetadataSelector, request: SessionRunMetadataPageRequest, signal?: AbortSignal) => {
    ensureValidSessionRunMetadataRequest(selector, request);
    const identity = selector.kind === "Session" ? selector.sessionId : selector.runAnchorId;
    const path = selector.kind === "Session" ? `/api/sessions/${identity}/runs/page` : `/api/sessions/by-run/${identity}/runs/page`;
    const query = new URLSearchParams({ direction: request.direction, limit: String(request.limit) });
    if (request.cursor !== undefined) query.set("cursor", request.cursor);
    const value = await fetchJson<unknown>(`${path}?${query}`, { signal });
    return decodeSessionRunMetadataPage(value, selector, request);
  },

  /// Rename a session's thread title — 204 on success, 404 when foreign / missing. The backend sanitises + truncates.
  renameSession: (sessionId: string, title: string) =>
    fetchJson<void>(`/api/sessions/${sessionId}`, { method: "PATCH", body: JSON.stringify({ title }) }),

  /// The Session Room for the session a run belongs to, focused on that run's turn — null when the run has no session (404).
  getRunRoom: async (runId: string): Promise<RoomView | null> => {
    try {
      return await fetchJson<RoomView>(`/api/sessions/by-run/${runId}/room`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// The Session Room for a session, focused on `focusRunId`'s turn when given (else the latest turn).
  getSessionRoom: (sessionId: string, focusRunId?: string) =>
    fetchJson<RoomView>(`/api/sessions/${sessionId}/room${focusRunId ? `?focusRunId=${encodeURIComponent(focusRunId)}` : ""}`),

  /// The Session Journal for the session a run belongs to, focused on that run's turn — null when the run has no session (404).
  /// Pass `since` (a prior response's `cursor`) for the DELTA: the response then omits the steps that cursor proves the
  /// caller already holds, and `mergeJournalDelta` reconciles it. Without it the whole session's walk comes back.
  getRunJournal: async (runId: string, since?: string): Promise<JournalView | null> => {
    try {
      const delta = since ? `?since=${encodeURIComponent(since)}` : "";
      return await fetchJson<JournalView>(`/api/sessions/by-run/${runId}/journal${delta}`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// The Session Journal for a session, focused on `focusRunId`'s turn when given (else the latest turn).
  getSessionJournal: (sessionId: string, focusRunId?: string) =>
    fetchJson<JournalView>(`/api/sessions/${sessionId}/journal${focusRunId ? `?focusRunId=${encodeURIComponent(focusRunId)}` : ""}`),

  /// A generic preview of one file a run's turn produced, keyed by its full repository + attempt identity. A path-only
  /// legacy identity remains supported only when the backend can resolve it unambiguously.
  getRoomFile: async (runId: string, file: RoomFileIdentity): Promise<RoomFilePreview | null> => {
    try {
      return await fetchJson<RoomFilePreview>(roomFileUrl(runId, file));
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// One model call's full detail (prompt · result · usage · trace) for the journal drawer, by the completed interaction
  /// record's ledger sequence. Null when the run is foreign / missing or the sequence isn't a model call (404).
  getModelCallDetail: async (runId: string, sequence: number): Promise<ModelCallDetail | null> => {
    try {
      return await fetchJson<ModelCallDetail>(`/api/sessions/by-run/${runId}/model-call/${sequence}`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// Answer the run's pending plan-confirmation card (S3 gate): approve releases execution; a non-approve answer
  /// carries the operator's revision feedback (the supervisor authors a revised plan version). Null when nothing is
  /// pending (already answered / not parked / foreign run — 404).
  confirmRunPlan: async (runId: string, body: { approve: boolean; feedback?: string }): Promise<WorkPlanConfirmationOutcome | null> => {
    try {
      return await fetchJson<WorkPlanConfirmationOutcome>(`/api/workflows/runs/${runId}/plan/confirm`, { method: "POST", body: JSON.stringify(body) });
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// Answer the run's NEWEST pending supervisor ASK (a content question or a review-gate escalation) straight from
  /// the run page — resolves the SAME durable wait the conversation card's Answer button does (first answer wins).
  /// Null when nothing is pending (already answered / not parked / foreign run — 404).
  /// `decision` is the STRUCTURED verdict for a GATE card (approve | revise | reject) — the gate rules on it instead
  /// of matching the leading word of the answer text, so a non-English approval is no longer read as feedback. Omit it
  /// for a content question, which has no verdict to give.
  answerRunAsk: async (runId: string, answer: string, decision?: SupervisorAnswerDecision): Promise<{ resumed: boolean } | null> => {
    try {
      return await fetchJson<{ resumed: boolean }>(`/api/workflows/runs/${runId}/ask/answer`, { method: "POST", body: JSON.stringify(decision ? { answer, decision } : { answer }) });
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// The thread a run belongs to, anchored at that run's turn — null when the run has no session (404). For the
  /// run-detail → session entry: any run (a turn or a rerun attempt) resolves to the same thread.
  getRunSession: async (runId: string): Promise<SessionDetail | null> => {
    try {
      return await fetchJson<SessionDetail>(`/api/workflows/runs/${runId}/session`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },
};

const RUN_STATUSES = new Set<WorkflowRunStatus>(["Pending", "Enqueued", "Running", "Success", "Failure", "Cancelled", "Suspended"]);
const REQUEST_STATUSES = new Set<WorkflowRunRequestStatus>(["Received", "Verified", "Normalized", "Matched", "Consumed", "Rejected"]);
const TEXT_STATES = new Set<SessionRunMetadataTextState>(["None", "Complete", "Truncated", "Corrupt"]);
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_CURSOR_LENGTH = 512;

function ensureValidSessionRunMetadataRequest(selector: SessionRunMetadataSelector, request: SessionRunMetadataPageRequest): void {
  const validSelector = selector.kind === "Session"
    ? validUuid(selector.sessionId) && selector.runAnchorId === null
    : selector.kind === "RunAnchor" && selector.sessionId === null && validUuid(selector.runAnchorId);
  const validDirection = request.direction === "Tail" || request.direction === "Older";
  const validCursor = request.direction === "Tail"
    ? request.cursor === undefined && request.membershipHeadRunNumber === undefined
    : typeof request.cursor === "string" && request.cursor.trim().length > 0 && request.cursor.length <= MAX_CURSOR_LENGTH
      && safeInteger(request.membershipHeadRunNumber, 1);
  if (!validSelector || !validDirection || !validCursor || !safeInteger(request.limit, 1) || request.limit > 256)
    throw new Error("Invalid Session run metadata page request.");
}

function decodeSessionRunMetadataPage(value: unknown, selector: SessionRunMetadataSelector, request: SessionRunMetadataPageRequest): SessionRunMetadataPage {
  const requestCursor = request.cursor ?? null;
  if (!isRecord(value) || !sameSelector(value.selector, selector) || !validUuid(value.sessionId) || value.direction !== request.direction
    || value.requestCursor !== requestCursor || value.limit !== request.limit || !safeInteger(value.membershipHeadRunNumber, 0)
    || value.consistency !== "MembershipHeadOnly" || !Array.isArray(value.items) || value.items.length > request.limit
    || !isRecord(value.omitted) || typeof value.omitted.older !== "boolean" || typeof value.omitted.newer !== "boolean"
    || !isRecord(value.continuation) || !(value.continuation.olderCursor === null || validOpaqueCursor(value.continuation.olderCursor))
    || typeof value.continuation.returnToTail !== "boolean") throw new InvalidSessionRunMetadataPageError();

  const head = Number(value.membershipHeadRunNumber);
  if ((request.direction === "Older" && head !== request.membershipHeadRunNumber)
    || (selector.kind === "Session" ? value.sessionId !== selector.sessionId || value.anchorRootRunId !== null : !validUuid(value.anchorRootRunId))
    || value.omitted.older !== (value.continuation.olderCursor !== null)
    || (value.omitted.older && value.items.length === 0)
    || value.omitted.newer !== (request.direction === "Older")
    || value.continuation.returnToTail !== (request.direction === "Older")) throw new InvalidSessionRunMetadataPageError();

  const items: SessionRunMetadataItem[] = [];
  let previous = 0;
  for (const candidate of value.items) {
    const decoded = decodeSessionRunMetadataItem(candidate, head);
    if (decoded.runNumber <= previous) throw new InvalidSessionRunMetadataPageError();
    previous = decoded.runNumber;
    items.push(decoded);
  }

  return {
    selector,
    sessionId: value.sessionId,
    direction: request.direction,
    requestCursor,
    limit: request.limit,
    membershipHeadRunNumber: head,
    anchorRootRunId: value.anchorRootRunId as string | null,
    consistency: "MembershipHeadOnly",
    items,
    omitted: { older: value.omitted.older, newer: value.omitted.newer },
    continuation: { olderCursor: value.continuation.olderCursor as string | null, returnToTail: value.continuation.returnToTail },
  };
}

function decodeSessionRunMetadataItem(value: unknown, head: number): SessionRunMetadataItem {
  if (!isRecord(value) || !validUuid(value.runId) || !safeInteger(value.runNumber, 1) || Number(value.runNumber) > head
    || !validUuid(value.runRequestId) || !(value.rootRunId === null || validUuid(value.rootRunId))
    || !(value.sessionTurnIndex === null || safeInteger(value.sessionTurnIndex, 1))
    || typeof value.status !== "string" || !RUN_STATUSES.has(value.status as WorkflowRunStatus)
    || !validDate(value.createdDate) || !(value.startedAt === null || validDate(value.startedAt)) || !(value.completedAt === null || validDate(value.completedAt))
    || typeof value.requestStatus !== "string" || !REQUEST_STATUSES.has(value.requestStatus as WorkflowRunRequestStatus) || !validDate(value.requestReceivedAt))
    throw new InvalidSessionRunMetadataPageError();

  return {
    runId: value.runId,
    runNumber: Number(value.runNumber),
    runRequestId: value.runRequestId,
    rootRunId: value.rootRunId as string | null,
    sessionTurnIndex: value.sessionTurnIndex as number | null,
    status: value.status as WorkflowRunStatus,
    projectionKind: decodeBoundedText(value.projectionKind, 128),
    sourceType: decodeBoundedText(value.sourceType, 128),
    rerunFromNodeId: decodeBoundedText(value.rerunFromNodeId, 256),
    createdDate: value.createdDate,
    startedAt: value.startedAt as string | null,
    completedAt: value.completedAt as string | null,
    error: decodeBoundedText(value.error, 512),
    requestStatus: value.requestStatus as WorkflowRunRequestStatus,
    requestReceivedAt: value.requestReceivedAt,
  };
}

function decodeBoundedText(value: unknown, maximumBytes: number): SessionRunMetadataText {
  if (!isRecord(value) || typeof value.state !== "string" || !TEXT_STATES.has(value.state as SessionRunMetadataTextState)
    || !safeInteger(value.sizeBytes, 0) || !(value.text === null || typeof value.text === "string")) throw new InvalidSessionRunMetadataPageError();
  const state = value.state as SessionRunMetadataTextState;
  const sizeBytes = Number(value.sizeBytes);
  const text = value.text as string | null;
  const encoded = text === null ? null : new TextEncoder().encode(text);
  const wellFormed = text === null || new TextDecoder("utf-8", { fatal: true }).decode(encoded!) === text;
  const valid = state === "None" ? text === null && sizeBytes === 0
    : state === "Corrupt" ? text === null
      : text !== null && wellFormed && encoded!.byteLength <= maximumBytes
        && (state === "Complete" ? encoded!.byteLength === sizeBytes : state === "Truncated" && encoded!.byteLength < sizeBytes && sizeBytes > maximumBytes);
  if (!valid) throw new InvalidSessionRunMetadataPageError();
  return { text, sizeBytes, state };
}

function sameSelector(value: unknown, expected: SessionRunMetadataSelector): boolean {
  if (!isRecord(value) || value.kind !== expected.kind) return false;
  return expected.kind === "Session"
    ? value.sessionId === expected.sessionId && value.runAnchorId === null
    : value.sessionId === null && value.runAnchorId === expected.runAnchorId;
}

function validOpaqueCursor(value: unknown): value is string { return typeof value === "string" && value.trim().length > 0 && value.length <= MAX_CURSOR_LENGTH; }
function validUuid(value: unknown): value is string { return typeof value === "string" && UUID.test(value); }
function safeInteger(value: unknown, minimum: number): value is number { return Number.isSafeInteger(value) && Number(value) >= minimum; }
function validDate(value: unknown): value is string { return typeof value === "string" && Number.isFinite(Date.parse(value)); }
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
