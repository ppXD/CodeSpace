import { ApiError, fetchJson, fetchResponse } from "./request";

// ─── Types (mirror backend AgentDefinition DTOs) ────────────────────────────────

export type AgentDefinitionOrigin = "Authored" | "Imported";

/**
 * Mirrors backend `AgentBoundSkill` — a skill bound to a persona (the Level-1 handle the UI renders as a chip),
 * read from the AgentSkillBinding join. The relational replacement for the dropped `skills_jsonb` blob.
 */
export interface AgentBoundSkill {
  skillDefinitionId: string;
  slug: string;
  name: string;
}

/**
 * The authorable surface of a persona (mirrors backend `AgentDefinitionInput`) — the create/update wire shape.
 * The @-mention handle is DERIVED from `name` server-side (never sent). Skills / MCP / provenance are
 * import-owned and intentionally absent — authoring never touches them. `model` null = the harness's default;
 * `tools` null = the harness default toolset, [] = no tools, non-empty = exactly these.
 */
export interface AgentDefinitionInput {
  name: string;
  description?: string | null;
  systemPrompt?: string | null;
  model?: string | null;
  defaultAutonomy?: string | null;
  tools?: string[] | null;
}

/**
 * Mirrors backend `AgentDefinitionSummary` — a reusable Agent persona (the canonical "Agent" noun).
 * The @-mention `slug` is the stable handle an `agent.run` node references; `tools` is null when the
 * persona inherits the harness's default toolset (distinct from an empty list = no tools). `boundSkills`
 * are the skills the persona carries (the binding join, ordered by handle).
 */
export interface AgentDefinitionSummary {
  id: string;
  teamId: string;
  slug: string;
  name: string;
  description: string | null;
  systemPrompt: string;
  model: string | null;
  defaultAutonomy: string | null;
  tools: string[] | null;
  origin: AgentDefinitionOrigin;
  /** The source pack's owner/repo for an imported persona (null for authored, or imported whose pack was removed). */
  packName: string | null;
  boundSkills: AgentBoundSkill[];
  createdDate: string;
}

/**
 * Mirrors backend `HarnessSummary` — one agent harness registered in the engine. `kind` is the wire
 * value the `agent.run` node stores (e.g. "codex-cli", "claude-code"); `models` seeds the model
 * field's suggestions for the chosen harness. Deployment-level, so the same set for every team.
 */
export interface HarnessSummary {
  kind: string;
  version: string;
  models: string[];
  /** Provider tags this harness can authenticate with (empty if it implements no projector) — used to filter the credential picker. */
  supportedProviders: string[];
}

export type AgentRunStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "TimedOut" | "Cancelled" | "NeedsReview";

/** Bounded, observation-only process history for one durable harness execution. */
export interface AgentRunHarnessProcessAttemptSummary {
  id: string;
  attemptOrdinal: number;
  state: string;
  startedAt: string;
  lastObservedAt: string;
  exitedAt: string | null;
  exitCode: number | null;
  errorCode: string | null;
}

/** Latest durable harness execution. Its state is physical-process truth, never the Agent Run verdict. */
export interface AgentRunHarnessExecutionSummary {
  id: string;
  generation: number;
  harnessTypeKey: string;
  runnerKind: string;
  state: string;
  attemptCount: number;
  /** Indexed existence observation; the drawer never performs an unbounded count over a long native stream. */
  hasCapturedNativeRecords: boolean;
  terminalAt: string | null;
  attempts: AgentRunHarnessProcessAttemptSummary[];
  attemptsTruncated: boolean;
}

export type AgentRunCaptureGapReadAvailability = "Available" | "BackendUnavailable";

/** One known-missing span exactly attributed to this Agent Run and frozen harness process attempt. */
export interface AgentRunCaptureGapSummary {
  id: string;
  agentRunId: string;
  harnessExecutionId: string;
  harnessProcessAttemptId: string;
  attemptWorkerFenceEpoch: number;
  subjectKind: string;
  subjectId: string | null;
  streamId: string | null;
  channel: string | null;
  rangeKind: string;
  rangeStart: number | null;
  rangeEnd: number | null;
  rangeStartedAt: string | null;
  rangeEndedAt: string | null;
  reason: string;
  reasonDetail: string | null;
  captureSource: string;
  noticedAt: string;
  resolution: string;
  recoveredAt: string | null;
  recoveredByKind: string | null;
  recoveredById: string | null;
}

/** Bounded newest-first observation; BackendUnavailable is distinct from an available empty page. */
export interface AgentRunCaptureGapObservation {
  availability: AgentRunCaptureGapReadAvailability;
  items: AgentRunCaptureGapSummary[];
  truncated: boolean;
  errorCode: string | null;
}

/** Mirrors backend `AgentRunSummary` — one agent run's live status + timing (no secret). */
export interface AgentRunSummary {
  id: string;
  status: AgentRunStatus;
  harness: string;
  /** The goal the agent was given — its instruction/prompt (a supervisor-spawned agent's per-subtask instruction, or an agent.run node's configured goal). null/absent when the task blob is missing. */
  goal?: string | null;
  error: string | null;
  startedAt: string | null;
  heartbeatAt: string | null;
  completedAt: string | null;
  createdDate: string;
  /** Observation-only durable harness/process facts; absent when capture was unavailable. */
  harnessExecution?: AgentRunHarnessExecutionSummary | null;
  /** Observation-only known-missing spans; unavailable never means no gaps. */
  captureGaps?: AgentRunCaptureGapObservation | null;
}

/** Mirrors backend `AgentRunEventDto` — one step in the run's append-only live log. */
export interface AgentRunEventDto {
  sequence: number;
  kind: string;
  text: string;
  data: string | null;
  /** Metadata-only ref for a large, already-redacted harness-native payload. Bytes are fetched only from the exact run/event reader. */
  dataArtifactId?: string | null;
  occurredAt: string;
}

export type AgentRunEventPageMode = "Tail" | "Older" | "Newer";

export interface AgentRunEventPageRequest {
  mode: AgentRunEventPageMode;
  cursor?: string;
  limit: number;
  /** Optional exact, open normalized event-kind discriminator. */
  kindFilter?: string;
}

/** One validated page plus the exact request identity that selected it; raw event payload strings/refs are never rewritten. */
export interface AgentRunEventPageResponse {
  agentRunId: string;
  mode: AgentRunEventPageMode;
  requestCursor: string | null;
  kindFilter: string | null;
  items: AgentRunEventDto[];
  hasOlder: boolean;
  hasNewer: boolean;
  nextOlderCursor: string | null;
  nextNewerCursor: string;
}

/** A non-retryable page-contract violation; transport/backend failures remain separately retryable. */
export class InvalidAgentRunEventPageError extends Error {
  constructor() {
    super("Invalid Agent Run event page response.");
    this.name = "InvalidAgentRunEventPageError";
  }
}

export type AgentRunEventDataReadAvailability = "NotReferenced" | "InvalidRange" | "MetadataMissing" | "PhysicalObjectMissing" | "IntegrityFailure" | "BackendUnavailable" | "AccessDenied";

export interface AgentRunEventDataRangeAvailable {
  availability: "Available";
  bytes: Uint8Array;
  agentRunId: string;
  eventSequence: number;
  dataArtifactId: string;
  offsetBytes: number;
  nextOffsetBytes: number | null;
  totalBytes: number;
  sha256: string;
  contentType: string;
  integrityVerified: boolean;
}

export interface AgentRunEventDataRangeProblem {
  availability: AgentRunEventDataReadAvailability | "Missing" | "InvalidResponse";
  code: string;
  isRetryable: boolean;
}

export type AgentRunEventDataRangeResult = AgentRunEventDataRangeAvailable | AgentRunEventDataRangeProblem;

export type AgentRunLogStatus = "Open" | "Completed" | "Truncated" | "Unavailable" | "Corrupt" | "CaptureFailed";

/** Metadata-only durable stream identity. Body bytes are always fetched separately in bounded ranges. */
export interface AgentRunLogStreamSummary {
  streamId: string;
  agentRunId: string;
  streamKind: string;
  contentType: string;
  contentEncoding: string | null;
  captureSource: string;
  retention: string;
  status: AgentRunLogStatus;
  revision: number;
  segmentCount: number;
  totalBytes: number;
  sha256: string | null;
  createdAt: string;
  lastModifiedAt: string;
  completedAt: string | null;
  errorCode: string | null;
}

export interface AgentRunLogPage {
  items: AgentRunLogStreamSummary[];
  nextCursor: string | null;
}

export type AgentRunLogReadAvailability = "InvalidRange" | "PhysicalObjectMissing" | "IntegrityFailure" | "BackendUnavailable" | "AccessDenied" | "ProviderTimeout" | "Unsupported";

export interface AgentRunLogRangeAvailable {
  availability: "Available";
  bytes: Uint8Array;
  offsetBytes: number;
  nextOffsetBytes: number;
  totalBytes: number;
  hasMore: boolean;
  revision: number;
  contentType: string;
  contentEncoding: string | null;
}

export interface AgentRunLogRangeProblem {
  availability: AgentRunLogReadAvailability | "Missing" | "InvalidResponse";
  code: string;
  isRetryable: boolean;
}

export type AgentRunLogRangeResult = AgentRunLogRangeAvailable | AgentRunLogRangeProblem;

/** Mirrors backend `ToolCallLedgerStatus` — the lifecycle outcome of one governed tool call. */
export type ToolCallLedgerStatus =
  | "Pending"
  | "Succeeded"
  | "Failed"
  | "Denied"
  | "AwaitingApproval"
  | "Running"
  | "Expired";

/**
 * Mirrors backend `ToolCallView` — one audit row of a side-effecting MCP tool call an agent run made:
 * what tool, the outcome, when, and the approval trail. Read-only + team-scoped at the source (the API
 * returns [] for a foreign/unknown run). `error` is already redacted at persist; read-only tools never
 * reach the ledger, so they're absent here.
 */
export interface ToolCallView {
  toolKind: string;
  status: ToolCallLedgerStatus;
  createdDate: string;
  lastModifiedDate: string;
  error: string | null;
  approvedByUserId: string | null;
  approvedAt: string | null;
}

/**
 * Mirrors backend `HarnessScore` — the success + latency rollup for one harness (or, with harness `"(all)"`,
 * across every harness). `total` counts only TERMINAL runs (Succeeded / Failed / Cancelled / TimedOut); a
 * still-running run is not scored. `successRate` is `succeeded / total` in 0..1 (0 when there are no terminal
 * runs). The P50/P95 durations are the median / 95th-percentile run length in seconds over the runs that have
 * one, or null when none do. Cost/token is deliberately ABSENT — the backend scorer does not aggregate it yet
 * (token usage lives in the per-run result envelope but isn't rolled up), so surfacing a number would fabricate it.
 */
export interface HarnessScore {
  harness: string;
  total: number;
  succeeded: number;
  successRate: number;
  p50DurationSeconds: number | null;
  p95DurationSeconds: number | null;
}

/**
 * Mirrors backend `AgentRunScorecard` — the team's per-harness + overall success/latency view, the measurement
 * spine that turns "is the agent working" into an auditable number. `harnesses` is sorted by harness name;
 * `overall` is the rollup across them all (its `harness` is `"(all)"`). Team-scoped at the source.
 */
export interface AgentRunScorecard {
  harnesses: HarnessScore[];
  overall: HarnessScore;
}

/** Optional filters the scorecard query supports — a trend window (`since`, ISO) and/or a single harness. */
export interface ScorecardFilters {
  since?: string;
  harness?: string;
}

/**
 * Mirrors backend `TeamCostRollup` — the team's token + estimated-USD spend over its agent runs. `estimatedCostUsd`
 * is null when nothing in the window could be priced (distinct from 0 = priced but free); `unknownCostRuns` is the
 * fail-open honesty qualifier (runs with no captured usage or an unpriceable model). The summed totals cover the
 * full window. (The per-run breakdown is omitted here — the library strip needs only the totals.)
 */
export interface TeamCostRollup {
  totalInputTokens: number;
  totalOutputTokens: number;
  estimatedCostUsd: number | null;
  runCount: number;
  unknownCostRuns: number;
  windowRunCount: number;
  truncated: boolean;
}

/**
 * Mirrors backend `AgentStat` — one persona's run evidence for its roster row. `total` counts only TERMINAL runs
 * (the success denominator; an in-flight run isn't scored). `recentOutcomes` is the persona's last runs' statuses
 * oldest→newest (a sparkline the row renders left-to-right, in-flight runs included). `estimatedCostUsd` is null when
 * nothing was priceable (distinct from 0 = priced but free); `unknownCostRuns` is the honesty qualifier on it.
 * `lastRunAt` (ISO) is always present — a persona appears only if it has at least one run.
 */
export interface AgentStat {
  agentDefinitionId: string;
  total: number;
  succeeded: number;
  successRate: number;
  p50DurationSeconds: number | null;
  p95DurationSeconds: number | null;
  estimatedCostUsd: number | null;
  unknownCostRuns: number;
  lastRunAt: string;
  recentOutcomes: AgentRunStatus[];
}

/**
 * Mirrors backend `AgentStatsRollup` — one `AgentStat` per persona that has runs in the window. The roster joins
 * these onto its persona list by `agentDefinitionId`; a persona with no entry has simply had no runs (its row
 * renders an empty state). Team-scoped at the source (the X-Team-Id header), keyed only by the `since` window.
 */
export interface AgentStatsRollup {
  agents: AgentStat[];
}

/** A run is still in flight (worth polling) while Queued or Running; terminal states stop the poll. */
export const isAgentRunActive = (status: AgentRunStatus | undefined): boolean =>
  status === "Queued" || status === "Running";

/**
 * Merge a freshly-fetched batch of run events into the accumulated live log, deduped + ordered by the
 * monotonic DB-assigned `sequence`. The log is append-only + immutable, so a higher sequence is strictly
 * newer and an existing sequence never changes; the dedup is defensive against a cursor overlap (re-fetching
 * a sequence we already hold). Returns `prev` UNCHANGED (same reference) when nothing new arrived, so a quiet
 * poll tick causes no re-render.
 */
export function mergeRunEvents(prev: AgentRunEventDto[], fresh: AgentRunEventDto[]): AgentRunEventDto[] {
  if (fresh.length === 0) return prev;

  const bySequence = new Map<number, AgentRunEventDto>();
  for (const e of prev) bySequence.set(e.sequence, e);
  for (const e of fresh) bySequence.set(e.sequence, e);

  // Same count ⇒ no new sequence (fresh fully overlapped) ⇒ keep the prev reference for render stability.
  if (bySequence.size === prev.length) return prev;

  return [...bySequence.values()].sort((a, b) => a.sequence - b.sequence);
}

/** The highest sequence in an accumulated log — the cursor to fetch the next delta from (0 when empty). */
export function lastEventSequence(events: AgentRunEventDto[]): number {
  return events.reduce((max, e) => (e.sequence > max ? e.sequence : max), 0);
}

// ─── API client ────────────────────────────────────────────────────────────────

export const agentsApi = {
  listAgentDefinitions: () => fetchJson<AgentDefinitionSummary[]>("/api/agents"),
  getAgentDefinition: (id: string) => fetchJson<AgentDefinitionSummary>(`/api/agents/${id}`),
  createAgentDefinition: (input: AgentDefinitionInput) =>
    fetchJson<{ id: string }>("/api/agents", { method: "POST", body: JSON.stringify(input) }),
  // Copy a Library store snapshot into a new working bench persona (the New-agent "from Library" path).
  instantiateAgentFromStore: (sourceDefinitionId: string) =>
    fetchJson<{ id: string }>("/api/agents/from-store", { method: "POST", body: JSON.stringify({ sourceDefinitionId }) }),
  // Author a new agent directly INTO the Library (a store entry under the team's Custom pack), not onto the bench.
  authorStoreAgent: (input: { name: string; description?: string | null; systemPrompt?: string | null }) =>
    fetchJson<{ id: string }>("/api/agents/library", { method: "POST", body: JSON.stringify(input) }),
  // agentDefinitionId is duplicated in the URL + body: the body must carry it so the command's `required
  // AgentDefinitionId` deserialization succeeds, and the controller then overrides it with the URL value via
  // `command with { AgentDefinitionId = id }` (the URL is authoritative). Same pattern as the variables PUTs.
  updateAgentDefinition: (id: string, input: AgentDefinitionInput) =>
    fetchJson<void>(`/api/agents/${id}`, { method: "PUT", body: JSON.stringify({ ...input, agentDefinitionId: id }) }),
  // Full-replace the persona's bound skills. agentDefinitionId is duplicated in the URL + body (same Rule-17
  // reason as the PUT above — the body satisfies the command's required member; the URL is authoritative).
  setAgentSkills: (id: string, skillDefinitionIds: string[]) =>
    fetchJson<void>(`/api/agents/${id}/skills`, { method: "PUT", body: JSON.stringify({ agentDefinitionId: id, skillDefinitionIds }) }),
  deleteAgentDefinition: (id: string) => fetchJson<void>(`/api/agents/${id}`, { method: "DELETE" }),
  listHarnesses: () => fetchJson<HarnessSummary[]>("/api/agents/harnesses"),
  getRun: (agentRunId: string) => fetchJson<AgentRunSummary>(`/api/agents/runs/${agentRunId}`),
  listRunEvents: (agentRunId: string, after = 0) =>
    fetchJson<AgentRunEventDto[]>(`/api/agents/runs/${agentRunId}/events?after=${after}`),
  pageRunEvents: async (agentRunId: string, request: AgentRunEventPageRequest, signal?: AbortSignal): Promise<AgentRunEventPageResponse> => {
    ensureValidEventPageRequest(request);
    const params = new URLSearchParams({ direction: request.mode, limit: String(request.limit) });
    if (request.cursor !== undefined) params.set("cursor", request.cursor);
    if (request.kindFilter !== undefined) params.set("kindFilter", request.kindFilter);
    const value = await fetchJson<unknown>(`/api/agents/runs/${encodeURIComponent(agentRunId)}/events/page?${params}`, { signal });
    return decodeAgentRunEventPage(value, agentRunId, request);
  },
  readRunEventDataRange: async (agentRunId: string, eventSequence: number, dataArtifactId: string, offsetBytes: number, limitBytes: number, signal?: AbortSignal): Promise<AgentRunEventDataRangeResult> => {
    const path = `/api/agents/runs/${encodeURIComponent(agentRunId)}/events/${eventSequence}/data?offsetBytes=${offsetBytes}&limitBytes=${limitBytes}`;
    try {
      const response = await fetchResponse(path, { signal, headers: { Accept: "application/octet-stream" } });
      const bytes = new Uint8Array(await response.arrayBuffer());
      return validEventDataRange(response.headers, bytes, { agentRunId, eventSequence, dataArtifactId, offsetBytes, limitBytes });
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      if (error.status === 404) return { availability: "Missing", code: error.code, isRetryable: false };
      return validEventDataProblem(error.body, { agentRunId, eventSequence, dataArtifactId });
    }
  },
  listRunLogs: async (agentRunId: string, cursor: string | null, limit = 25, signal?: AbortSignal): Promise<AgentRunLogPage | null> => {
    const normalizedLimit = Math.min(Math.max(Number.isSafeInteger(limit) ? limit : 25, 1), 100);
    const params = new URLSearchParams({ limit: String(normalizedLimit) });
    if (cursor) params.set("cursor", cursor);
    try {
      const value = await fetchJson<unknown>(`/api/agents/runs/${encodeURIComponent(agentRunId)}/logs?${params}`, { signal });
      return decodeAgentRunLogPage(value, agentRunId, cursor, normalizedLimit);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },
  getRunLog: async (agentRunId: string, streamId: string, signal?: AbortSignal): Promise<AgentRunLogStreamSummary | null> => {
    try {
      const value = await fetchJson<unknown>(`/api/agents/runs/${encodeURIComponent(agentRunId)}/logs/${encodeURIComponent(streamId)}`, { signal });
      return decodeAgentRunLogMetadata(value, agentRunId, streamId);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },
  readRunLogRange: async (agentRunId: string, streamId: string, offsetBytes: number, limitBytes: number, signal?: AbortSignal): Promise<AgentRunLogRangeResult> => {
    const path = `/api/agents/runs/${encodeURIComponent(agentRunId)}/logs/${encodeURIComponent(streamId)}/content?offsetBytes=${offsetBytes}&limitBytes=${limitBytes}`;
    try {
      const response = await fetchResponse(path, { signal, headers: { Accept: "application/octet-stream" } });
      const bytes = new Uint8Array(await response.arrayBuffer());
      return validLogRange(response.headers, bytes, offsetBytes);
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      if (error.status === 404) return { availability: "Missing", code: error.code, isRetryable: false };
      const problem = error.body as { availability?: unknown; code?: unknown; isRetryable?: unknown } | undefined;
      if (!isLogReadAvailability(problem?.availability) || typeof problem?.code !== "string" || typeof problem?.isRetryable !== "boolean") {
        return { availability: "InvalidResponse", code: `http_${error.status}_without_log_problem`, isRetryable: false };
      }
      return { availability: problem.availability, code: problem.code, isRetryable: problem.isRetryable };
    }
  },
  listToolCalls: (agentRunId: string) =>
    fetchJson<ToolCallView[]>(`/api/agents/runs/${agentRunId}/tool-calls`),
  getScorecard: (filters: ScorecardFilters = {}) => {
    const params = new URLSearchParams();
    if (filters.since) params.set("since", filters.since);
    if (filters.harness) params.set("harness", filters.harness);
    const qs = params.toString();
    return fetchJson<AgentRunScorecard>(`/api/agents/scorecard${qs ? `?${qs}` : ""}`);
  },
  getCost: () => fetchJson<TeamCostRollup>("/api/agents/cost"),
  // Per-agent run stats for the roster rows — grouped by persona, optionally windowed. Mirrors getScorecard's
  // since-passing (the window the roster's time control finally feeds).
  getStats: (since?: string) =>
    fetchJson<AgentStatsRollup>(`/api/agents/stats${since ? `?since=${encodeURIComponent(since)}` : ""}`),
};

const LOG_READ_AVAILABILITIES = new Set<AgentRunLogReadAvailability>(["InvalidRange", "PhysicalObjectMissing", "IntegrityFailure", "BackendUnavailable", "AccessDenied", "ProviderTimeout", "Unsupported"]);
const EVENT_DATA_READ_AVAILABILITIES = new Set<AgentRunEventDataReadAvailability>(["NotReferenced", "InvalidRange", "MetadataMissing", "PhysicalObjectMissing", "IntegrityFailure", "BackendUnavailable", "AccessDenied"]);
const AGENT_EVENT_KINDS = new Set(["Queued", "Started", "AssistantMessage", "Reasoning", "PlanUpdate", "ToolCall", "CommandExecuted", "FileChanged", "TestOutput", "ApprovalRequested", "ApprovalResolved", "Warning", "Error", "FinalSummary", "Completed"]);
const LOG_STATUSES = new Set<AgentRunLogStatus>(["Open", "Completed", "Truncated", "Unavailable", "Corrupt", "CaptureFailed"]);
const LOG_RETENTIONS = new Set(["Ephemeral", "Run", "Team", "Compliance", "Permanent"]);

function ensureValidEventPageRequest(request: AgentRunEventPageRequest): void {
  const cursor = request.cursor === undefined ? null : exactEventCursor(request.cursor, request.mode === "Older");
  const validMode = request.mode === "Tail" || request.mode === "Older" || request.mode === "Newer";
  const validCursorShape = request.mode === "Tail" ? request.cursor === undefined : cursor !== null;
  const validKindFilter = request.kindFilter === undefined || (typeof request.kindFilter === "string" && request.kindFilter.trim().length > 0 && request.kindFilter.length <= 128);
  if (!validMode || !validCursorShape || !validKindFilter || !Number.isSafeInteger(request.limit) || request.limit < 1 || request.limit > 500)
    throw new Error("Invalid Agent Run event page request.");
}

function decodeAgentRunEventPage(value: unknown, agentRunId: string, request: AgentRunEventPageRequest): AgentRunEventPageResponse {
  const requestCursor = request.cursor ?? null;
  const kindFilter = request.kindFilter ?? null;
  if (!isRecord(value) || value.agentRunId !== agentRunId || value.mode !== request.mode || value.requestCursor !== requestCursor
    || value.kindFilter !== kindFilter
    || !Array.isArray(value.items) || value.items.length > request.limit || typeof value.hasOlder !== "boolean" || typeof value.hasNewer !== "boolean"
    || !(value.nextOlderCursor === null || typeof value.nextOlderCursor === "string") || typeof value.nextNewerCursor !== "string")
    throw new InvalidAgentRunEventPageError();

  const cursor = requestCursor === null ? null : exactEventCursor(requestCursor, request.mode === "Older");
  const items: AgentRunEventDto[] = [];
  let previousSequence = 0;
  for (const candidate of value.items) {
    if (!isRecord(candidate) || !Number.isSafeInteger(candidate.sequence) || Number(candidate.sequence) <= 0 || Number(candidate.sequence) <= previousSequence
      || typeof candidate.kind !== "string" || !AGENT_EVENT_KINDS.has(candidate.kind) || (kindFilter !== null && candidate.kind !== kindFilter) || typeof candidate.text !== "string"
      || !(candidate.data === null || typeof candidate.data === "string")
      || !(candidate.dataArtifactId === null || (typeof candidate.dataArtifactId === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(candidate.dataArtifactId)))
      || typeof candidate.occurredAt !== "string" || !Number.isFinite(Date.parse(candidate.occurredAt)))
      throw new InvalidAgentRunEventPageError();

    const sequence = Number(candidate.sequence);
    if ((request.mode === "Older" && (cursor === null || sequence >= cursor)) || (request.mode === "Newer" && (cursor === null || sequence <= cursor)))
      throw new InvalidAgentRunEventPageError();

    items.push({ sequence, kind: candidate.kind, text: candidate.text, data: candidate.data, dataArtifactId: candidate.dataArtifactId, occurredAt: candidate.occurredAt });
    previousSequence = sequence;
  }

  const first = items[0]?.sequence;
  const last = items.at(-1)?.sequence;
  const nextOlder = value.nextOlderCursor === null ? null : exactEventCursor(value.nextOlderCursor, true);
  const nextNewer = exactEventCursor(value.nextNewerCursor, false);
  const expectedOlder = value.hasOlder ? first ?? (request.mode === "Newer" ? cursor : null) : null;
  const expectedNewer = last ?? (request.mode === "Older" && cursor !== null ? cursor - 1 : cursor ?? 0);
  if (nextOlder !== expectedOlder || nextNewer !== expectedNewer || (value.hasOlder && expectedOlder === null) || (value.hasNewer && items.length === 0) || (request.mode === "Tail" && value.hasNewer))
    throw new InvalidAgentRunEventPageError();

  return {
    agentRunId,
    mode: request.mode,
    requestCursor,
    kindFilter,
    items,
    hasOlder: value.hasOlder,
    hasNewer: value.hasNewer,
    nextOlderCursor: nextOlder === null ? null : String(nextOlder),
    nextNewerCursor: String(nextNewer),
  };
}

function exactEventCursor(value: unknown, positive: boolean): number | null {
  if (typeof value !== "string" || !/^\d+$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= (positive ? 1 : 0) && String(parsed) === value ? parsed : null;
}

function isLogReadAvailability(value: unknown): value is AgentRunLogReadAvailability {
  return typeof value === "string" && LOG_READ_AVAILABILITIES.has(value as AgentRunLogReadAvailability);
}

function validLogRange(headers: Headers, bytes: Uint8Array, requestedOffset: number): AgentRunLogRangeResult {
  const offsetBytes = exactIntegerHeader(headers, "X-CodeSpace-Log-Offset");
  const nextOffsetBytes = exactIntegerHeader(headers, "X-CodeSpace-Log-Next-Offset");
  const totalBytes = exactIntegerHeader(headers, "X-CodeSpace-Log-Total-Bytes");
  const revision = exactIntegerHeader(headers, "X-CodeSpace-Log-Revision");
  const hasMoreRaw = headers.get("X-CodeSpace-Log-Has-More");
  const contentType = headers.get("X-CodeSpace-Log-Content-Type");
  const valid = offsetBytes === requestedOffset && nextOffsetBytes != null && totalBytes != null && revision != null && contentType != null
    && nextOffsetBytes - offsetBytes === bytes.byteLength && totalBytes >= nextOffsetBytes && (hasMoreRaw === "true" || hasMoreRaw === "false")
    && (hasMoreRaw === "true") === (nextOffsetBytes < totalBytes) && !(hasMoreRaw === "true" && nextOffsetBytes === offsetBytes);
  if (!valid) return { availability: "InvalidResponse", code: "invalid_log_range_headers", isRetryable: false };
  return {
    availability: "Available",
    bytes,
    offsetBytes,
    nextOffsetBytes,
    totalBytes,
    hasMore: hasMoreRaw === "true",
    revision,
    contentType,
    contentEncoding: headers.get("X-CodeSpace-Log-Content-Encoding"),
  };
}

interface ExpectedEventDataIdentity {
  agentRunId: string;
  eventSequence: number;
  dataArtifactId: string;
  offsetBytes?: number;
  limitBytes?: number;
}

function validEventDataRange(headers: Headers, bytes: Uint8Array, expected: Required<ExpectedEventDataIdentity>): AgentRunEventDataRangeResult {
  const agentRunId = headers.get("X-CodeSpace-Agent-Run-Id");
  const eventSequence = exactIntegerHeader(headers, "X-CodeSpace-Agent-Event-Sequence");
  const dataArtifactId = headers.get("X-CodeSpace-Agent-Event-Data-Artifact-Id");
  const offsetBytes = exactIntegerHeader(headers, "X-CodeSpace-Agent-Event-Data-Offset");
  const nextOffsetRaw = headers.get("X-CodeSpace-Agent-Event-Data-Next-Offset");
  const nextOffsetBytes = nextOffsetRaw == null ? null : exactIntegerHeader(headers, "X-CodeSpace-Agent-Event-Data-Next-Offset");
  const totalBytes = exactIntegerHeader(headers, "X-CodeSpace-Agent-Event-Data-Total-Bytes");
  const sha256 = headers.get("X-CodeSpace-Agent-Event-Data-Sha256");
  const contentType = headers.get("X-CodeSpace-Agent-Event-Data-Content-Type");
  const integrityRaw = headers.get("X-CodeSpace-Agent-Event-Data-Integrity-Verified");
  const computedNext = offsetBytes == null ? null : offsetBytes + bytes.byteLength;
  const hasMore = computedNext != null && totalBytes != null && computedNext < totalBytes;
  const valid = agentRunId === expected.agentRunId && eventSequence === expected.eventSequence
    && dataArtifactId?.toLowerCase() === expected.dataArtifactId.toLowerCase() && offsetBytes === expected.offsetBytes
    && bytes.byteLength <= expected.limitBytes && computedNext != null && Number.isSafeInteger(computedNext) && totalBytes != null && computedNext <= totalBytes
    && !(bytes.byteLength === 0 && offsetBytes < totalBytes) && (hasMore ? nextOffsetRaw != null && nextOffsetBytes === computedNext : nextOffsetRaw == null)
    && typeof sha256 === "string" && /^[0-9a-f]{64}$/i.test(sha256) && typeof contentType === "string" && contentType.split(";", 1)[0].trim().toLowerCase() === "application/json"
    && (integrityRaw === "true" || integrityRaw === "false") && !(integrityRaw === "true" && (offsetBytes !== 0 || computedNext !== totalBytes));
  if (!valid) return { availability: "InvalidResponse", code: "invalid_event_data_range_headers", isRetryable: false };
  return {
    availability: "Available", bytes, agentRunId, eventSequence, dataArtifactId, offsetBytes, nextOffsetBytes,
    totalBytes, sha256, contentType, integrityVerified: integrityRaw === "true",
  };
}

function validEventDataProblem(value: unknown, expected: Pick<ExpectedEventDataIdentity, "agentRunId" | "eventSequence" | "dataArtifactId">): AgentRunEventDataRangeProblem {
  const availability = isRecord(value) && typeof value.availability === "string" ? value.availability : null;
  if (!isRecord(value) || value.agentRunId !== expected.agentRunId || value.eventSequence !== expected.eventSequence
    || typeof value.dataArtifactId !== "string" || value.dataArtifactId.toLowerCase() !== expected.dataArtifactId.toLowerCase()
    || availability == null || !EVENT_DATA_READ_AVAILABILITIES.has(availability as AgentRunEventDataReadAvailability)
    || typeof value.code !== "string" || value.code.length === 0 || typeof value.isRetryable !== "boolean"
    || value.isRetryable !== (availability === "BackendUnavailable")) {
    return { availability: "InvalidResponse", code: "invalid_event_data_problem", isRetryable: false };
  }
  return { availability: availability as AgentRunEventDataReadAvailability, code: value.code, isRetryable: value.isRetryable };
}

function exactIntegerHeader(headers: Headers, name: string): number | null {
  const raw = headers.get(name);
  if (raw == null || !/^\d+$/.test(raw)) return null;
  const parsed = Number(raw);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function decodeAgentRunLogPage(value: unknown, expectedRunId: string, cursor: string | null, limit: number): AgentRunLogPage {
  if (!isRecord(value) || !Array.isArray(value.items) || value.items.length > limit || !validCursor(value.nextCursor)) throw new Error("Agent Run log metadata pagination contract is invalid.");
  if (value.nextCursor != null && (value.items.length === 0 || value.nextCursor === cursor)) throw new Error("Agent Run log metadata pagination contract cannot advance.");
  const items = value.items.map((item) => decodeAgentRunLogMetadata(item, expectedRunId));
  if (new Set(items.map((item) => item.streamId)).size !== items.length) throw new Error("Agent Run log metadata pagination contract contains duplicate stream identities.");
  return { items, nextCursor: value.nextCursor };
}

function decodeAgentRunLogMetadata(value: unknown, expectedRunId: string, expectedStreamId?: string): AgentRunLogStreamSummary {
  if (!isRecord(value)) throw new Error("Agent Run log metadata contract is not an object.");
  const streamId = nonEmptyString(value.streamId);
  const agentRunId = nonEmptyString(value.agentRunId);
  const streamKind = nonEmptyString(value.streamKind);
  const contentType = nonEmptyString(value.contentType);
  const contentEncoding = nullableString(value.contentEncoding);
  const captureSource = nonEmptyString(value.captureSource);
  const retention = nonEmptyString(value.retention);
  const status = nonEmptyString(value.status);
  const revision = nonNegativeInteger(value.revision, false);
  const segmentCount = nonNegativeInteger(value.segmentCount);
  const totalBytes = nonNegativeInteger(value.totalBytes);
  const sha256 = nullableString(value.sha256);
  const createdAt = isoDate(value.createdAt);
  const lastModifiedAt = isoDate(value.lastModifiedAt);
  const completedAt = nullableIsoDate(value.completedAt);
  const errorCode = nullableString(value.errorCode);
  const identityValid = agentRunId === expectedRunId && (expectedStreamId == null || streamId === expectedStreamId);
  const enumValid = LOG_STATUSES.has(status as AgentRunLogStatus) && LOG_RETENTIONS.has(retention);
  const digestValid = sha256 == null || /^[0-9a-f]{64}$/i.test(sha256);
  const descriptorValid = /^[a-z0-9][a-z0-9._/-]{0,126}\/v[1-9][0-9]*$/.test(streamKind) && /^[a-z0-9][a-z0-9._/-]{0,126}\/v[1-9][0-9]*$/.test(captureSource)
    && /^[^\s/]+\/[^\s]+$/.test(contentType) && (contentEncoding == null || /^[a-z0-9][a-z0-9._+-]{0,63}$/i.test(contentEncoding));
  const lifecycleValid = status === "Open" ? completedAt == null && errorCode == null
    : status === "Completed" ? completedAt != null && errorCode == null : completedAt != null && errorCode != null;
  const timeValid = Date.parse(lastModifiedAt) >= Date.parse(createdAt) && (completedAt == null || Date.parse(lastModifiedAt) >= Date.parse(completedAt));
  if (!identityValid || !enumValid || !digestValid || !descriptorValid || !lifecycleValid || !timeValid) throw new Error("Agent Run log metadata contract has an invalid identity, enum, descriptor, lifecycle, or digest.");
  return { streamId, agentRunId, streamKind, contentType, contentEncoding, captureSource, retention, status: status as AgentRunLogStatus, revision, segmentCount, totalBytes, sha256, createdAt, lastModifiedAt, completedAt, errorCode };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value != null && !Array.isArray(value);
}

function nonEmptyString(value: unknown): string {
  if (typeof value !== "string" || value.length === 0) throw new Error("Agent Run log metadata contract requires a non-empty string.");
  return value;
}

function nullableString(value: unknown): string | null {
  if (value === null) return null;
  return nonEmptyString(value);
}

function nonNegativeInteger(value: unknown, allowZero = true): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < (allowZero ? 0 : 1)) throw new Error("Agent Run log metadata contract requires a bounded integer.");
  return value;
}

function isoDate(value: unknown): string {
  const parsed = nonEmptyString(value);
  if (!/^\d{4}-\d{2}-\d{2}T/.test(parsed) || !Number.isFinite(Date.parse(parsed))) throw new Error("Agent Run log metadata contract requires an ISO timestamp.");
  return parsed;
}

function nullableIsoDate(value: unknown): string | null {
  return value === null ? null : isoDate(value);
}

function validCursor(value: unknown): value is string | null {
  return value === null || (typeof value === "string" && value.length > 0 && value.length <= 8192);
}
