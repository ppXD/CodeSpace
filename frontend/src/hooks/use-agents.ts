import { useQuery } from "@tanstack/react-query";

import { agentsApi } from "@/api/agents";

/**
 * Agent-persona data hooks. The library list backs the editor's persona picker + (later) the Agents
 * library surface. Not keyed by team id — switching team invalidates the whole cache (see useActiveTeam),
 * so the X-Team-Id header change is enough.
 */

export function useAgentDefinitions() {
  return useQuery({
    queryKey: ["agent-definitions"],
    queryFn: () => agentsApi.listAgentDefinitions(),
  });
}
