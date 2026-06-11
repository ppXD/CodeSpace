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

// ─── API client ────────────────────────────────────────────────────────────────

export const agentsApi = {
  listAgentDefinitions: () => fetchJson<AgentDefinitionSummary[]>("/api/agents"),
  listHarnesses: () => fetchJson<HarnessSummary[]>("/api/agents/harnesses"),
};
