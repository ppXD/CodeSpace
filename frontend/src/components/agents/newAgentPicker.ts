import type { PackArtifactSummary, PackSummary } from "@/api/packs";

/** Packs with at least one store agent to instantiate — skill-only packs are irrelevant to "New agent from Library". */
export function packsWithAgents(packs: PackSummary[]): PackSummary[] {
  return packs.filter((p) => p.agentCount > 0);
}

/** A pack's AGENT artifacts, filtered by a case-insensitive name/handle query (a large pack can hold hundreds). */
export function agentArtifacts(artifacts: PackArtifactSummary[], query: string): PackArtifactSummary[] {
  const agents = artifacts.filter((a) => a.kind === "Agent");
  const q = query.trim().toLowerCase();

  if (!q) return agents;

  return agents.filter((a) => a.name.toLowerCase().includes(q) || a.slug.toLowerCase().includes(q));
}

/** Packs with at least one store skill — the source for the agent editor's skill-binding picker (agent-only packs are irrelevant). */
export function packsWithSkills(packs: PackSummary[]): PackSummary[] {
  return packs.filter((p) => p.skillCount > 0);
}

/** A pack's SKILL artifacts, filtered by a case-insensitive name/handle query. */
export function skillArtifacts(artifacts: PackArtifactSummary[], query: string): PackArtifactSummary[] {
  const skills = artifacts.filter((a) => a.kind === "Skill");
  const q = query.trim().toLowerCase();

  if (!q) return skills;

  return skills.filter((a) => a.name.toLowerCase().includes(q) || a.slug.toLowerCase().includes(q));
}
