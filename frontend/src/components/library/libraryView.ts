import type { PackArtifactSummary, PackSummary } from "@/api/packs";

/**
 * Pure view helpers for the Library/store page — kept out of the component so the rail subtitle and the
 * agent/skill split are unit-testable without rendering. No React, no fetching.
 */

/** Compact "N agents · M skills" subtitle for a pack row / detail header. Empty pack ⇒ "empty". */
export function countLabel(agentCount: number, skillCount: number): string {
  const parts: string[] = [];
  if (agentCount > 0) parts.push(`${agentCount} ${agentCount === 1 ? "agent" : "agents"}`);
  if (skillCount > 0) parts.push(`${skillCount} ${skillCount === 1 ? "skill" : "skills"}`);

  return parts.length === 0 ? "empty" : parts.join(" · ");
}

/** Split a pack's artifacts into its agent + skill sections, preserving the server's display order within each. */
export function splitArtifacts(artifacts: PackArtifactSummary[]): { agents: PackArtifactSummary[]; skills: PackArtifactSummary[] } {
  return {
    agents: artifacts.filter((a) => a.kind === "Agent"),
    skills: artifacts.filter((a) => a.kind === "Skill"),
  };
}

/**
 * Resolve which pack the detail pane shows: an explicit pick wins, but only while it still exists in the
 * current list; otherwise fall back to the first pack. This reconciles a stale pick (the picked pack was
 * removed/renamed server-side, so a later refetch dropped it) instead of leaving the rail with no active row
 * and the detail stranded on a 404 — and it stays a pure function so it needs no setState-in-effect.
 */
export function resolveSelectedPackId(picked: string | null, packs: PackSummary[]): string | null {
  if (picked && packs.some((p) => p.id === picked)) return picked;

  return packs[0]?.id ?? null;
}

/** Short source label for a pack header — `owner/repo` for github/git URLs, else the pack name. */
export function sourceLabel(pack: PackSummary): string {
  if (!pack.url) return pack.name;

  const stripped = pack.url.replace(/^https?:\/\//, "").replace(/\/+$/, "").replace(/\.git$/, "");
  const segments = stripped.split("/").filter(Boolean);

  return segments.length >= 3 ? segments.slice(-2).join("/") : stripped;
}
