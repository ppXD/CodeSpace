import type { AgentDefinitionSummary } from "@/api/agents";

/** The Agents library's origin filter — all personas, or only authored / only pack-imported ones. */
export type OriginFilter = "all" | "Authored" | "Imported";

/**
 * Client-side filter for the Agents library: by origin (all / authored / imported), then a case-insensitive
 * substring match on the persona's name, @-handle, or description. A blank query matches everything (within the
 * chosen origin). Pure so the list behaviour is unit-pinned without rendering the page.
 */
export function filterAgents(agents: AgentDefinitionSummary[], query: string, origin: OriginFilter): AgentDefinitionSummary[] {
  const q = query.trim().toLowerCase();

  return agents.filter((a) => {
    if (origin !== "all" && a.origin !== origin) return false;
    if (q.length === 0) return true;
    return (
      a.name.toLowerCase().includes(q) ||
      a.slug.toLowerCase().includes(q) ||
      (a.description?.toLowerCase().includes(q) ?? false)
    );
  });
}
