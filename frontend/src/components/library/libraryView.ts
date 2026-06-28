import type { PackSummary } from "@/api/packs";

/**
 * Pure view helpers for the Library/store page — kept out of the component so the rail subtitle, the detail-tab
 * reconciliation and the pack-selection are unit-testable without rendering. No React, no fetching.
 */

/** Compact "N agents · M skills" subtitle for a pack row / detail header. Empty pack ⇒ "empty". */
export function countLabel(agentCount: number, skillCount: number): string {
  const parts: string[] = [];
  if (agentCount > 0) parts.push(`${agentCount} ${agentCount === 1 ? "agent" : "agents"}`);
  if (skillCount > 0) parts.push(`${skillCount} ${skillCount === 1 ? "skill" : "skills"}`);

  return parts.length === 0 ? "empty" : parts.join(" · ");
}

/**
 * Which kind-tab the detail shows: the explicit pick, UNLESS that kind is now empty while the other has rows — then
 * fall back to the populated kind (mirrors resolveSelectedPackId reconciling a stale pack pick), so a sync that drops
 * every artifact of the pinned kind never strands the user on an empty tab. No pick yet ⇒ the populated kind (a
 * skill-only pack opens on Skills).
 */
export function resolveDetailTab(picked: "agents" | "skills" | null, agentCount: number, skillCount: number): "agents" | "skills" {
  if (picked === "agents" && agentCount === 0 && skillCount > 0) return "skills";
  if (picked === "skills" && skillCount === 0 && agentCount > 0) return "agents";

  return picked ?? (agentCount > 0 ? "agents" : "skills");
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
