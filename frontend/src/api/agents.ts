import { fetchJson } from "./request";

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
}

/** Mirrors backend `AgentRunEventDto` — one step in the run's append-only live log. */
export interface AgentRunEventDto {
  sequence: number;
  kind: string;
  text: string;
  data: string | null;
  occurredAt: string;
}

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
