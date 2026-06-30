import type { PackSummary } from "@/api/packs";

/** Packs with at least one store agent to instantiate — skill-only packs are irrelevant to "New agent from Library". */
export function packsWithAgents(packs: PackSummary[]): PackSummary[] {
  return packs.filter((p) => p.agentCount > 0);
}

/** Packs with at least one store skill — the source for the agent editor's skill-binding dropdown (agent-only packs are irrelevant). */
export function packsWithSkills(packs: PackSummary[]): PackSummary[] {
  return packs.filter((p) => p.skillCount > 0);
}
