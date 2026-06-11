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

/** A run is still in flight (worth polling) while Queued or Running; terminal states stop the poll. */
export const isAgentRunActive = (status: AgentRunStatus | undefined): boolean =>
  status === "Queued" || status === "Running";

// ─── API client ────────────────────────────────────────────────────────────────

export const agentsApi = {
  listAgentDefinitions: () => fetchJson<AgentDefinitionSummary[]>("/api/agents"),
  listHarnesses: () => fetchJson<HarnessSummary[]>("/api/agents/harnesses"),
  getRun: (agentRunId: string) => fetchJson<AgentRunSummary>(`/api/agents/runs/${agentRunId}`),
  listRunEvents: (agentRunId: string, after = 0) =>
    fetchJson<AgentRunEventDto[]>(`/api/agents/runs/${agentRunId}/events?after=${after}`),
};
