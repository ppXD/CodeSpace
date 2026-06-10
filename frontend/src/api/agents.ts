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

// ─── API client ────────────────────────────────────────────────────────────────

export const agentsApi = {
  listAgentDefinitions: () => fetchJson<AgentDefinitionSummary[]>("/api/agents"),
};
