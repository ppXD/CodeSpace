import type { PackSyncResult } from "@/api/packs";

/**
 * Pure helpers for the Sync result — kept out of the modal so the headline summary and the new-artifact count
 * are unit-testable. No React, no fetching.
 */

/** How many artifacts the sync discovered that aren't imported yet (across agents + skills). */
export function newArtifactCount(result: PackSyncResult): number {
  return result.newArtifacts.agents.length + result.newArtifacts.skills.length;
}

/**
 * Compact headline for a sync outcome — "12 up to date · 2 updated · 3 new", omitting the zero parts.
 * When nothing changed and nothing new was found, says so plainly.
 */
export function syncSummaryLabel(result: PackSyncResult): string {
  const created = newArtifactCount(result);

  if (result.updated === 0 && created === 0) return "Already up to date";

  const parts: string[] = [];
  if (result.upToDate > 0) parts.push(`${result.upToDate} up to date`);
  if (result.updated > 0) parts.push(`${result.updated} updated`);
  if (created > 0) parts.push(`${created} new`);

  return parts.join(" · ");
}
