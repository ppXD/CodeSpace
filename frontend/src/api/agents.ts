import { fetchJson } from "./request";

// ─── Types (mirror backend AgentDefinition DTOs) ────────────────────────────────

export type AgentDefinitionOrigin = "Authored" | "Imported";

/**
 * Mirrors backend `AgentDefinitionSummary` — a reusable Agent persona (the canonical "Agent" noun).
 * The @-mention `slug` is the stable handle an `agent.code` node references; `tools` is null when the
 * persona inherits the harness's default toolset (distinct from an empty list = no tools).
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
  createdDate: string;
}

/**
 * Mirrors backend `HarnessSummary` — one agent harness registered in the engine. `kind` is the wire
 * value the `agent.code` node stores (e.g. "codex-cli", "claude-code"); `models` seeds the model
 * field's suggestions for the chosen harness. Deployment-level, so the same set for every team.
 */
export interface HarnessSummary {
  kind: string;
  version: string;
  models: string[];
  /** Provider tags this harness can authenticate with (empty if it implements no projector) — used to filter the credential picker. */
  supportedProviders: string[];
}

export type AgentRunStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "TimedOut" | "Cancelled";

/** Mirrors backend `AgentRunSummary` — one agent run's live status + timing (no secret). */
export interface AgentRunSummary {
  id: string;
  status: AgentRunStatus;
  harness: string;
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
  listHarnesses: () => fetchJson<HarnessSummary[]>("/api/agents/harnesses"),
  getRun: (agentRunId: string) => fetchJson<AgentRunSummary>(`/api/agents/runs/${agentRunId}`),
  listRunEvents: (agentRunId: string, after = 0) =>
    fetchJson<AgentRunEventDto[]>(`/api/agents/runs/${agentRunId}/events?after=${after}`),
  listToolCalls: (agentRunId: string) =>
    fetchJson<ToolCallView[]>(`/api/agents/runs/${agentRunId}/tool-calls`),
};
