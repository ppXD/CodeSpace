import { useQuery } from "@tanstack/react-query";

import { agentsApi, isAgentRunActive } from "@/api/agents";

/**
 * Agent-persona data hooks. The library list backs the editor's persona picker + (later) the Agents
 * library surface. Not keyed by team id — switching team invalidates the whole cache (see useActiveTeam),
 * so the X-Team-Id header change is enough.
 */

export function useAgentDefinitions() {
  return useQuery({
    queryKey: ["agents"],
    queryFn: () => agentsApi.listAgentDefinitions(),
  });
}

/** The harnesses registered in the engine — deployment-level, so a long staleTime; backs the agent node's harness picker. */
export function useHarnesses() {
  return useQuery({
    queryKey: ["harnesses"],
    queryFn: () => agentsApi.listHarnesses(),
    staleTime: 5 * 60 * 1000,
  });
}

/** One agent run's live status — polls every 2s while the run is in flight (Queued/Running), stops once terminal. */
export function useAgentRun(agentRunId: string | undefined) {
  return useQuery({
    queryKey: ["agent-run", agentRunId],
    queryFn: () => agentsApi.getRun(agentRunId!),
    enabled: !!agentRunId,
    refetchInterval: (query) => (isAgentRunActive(query.state.data?.status) ? 2000 : false),
  });
}

/**
 * One agent run's live event log. Re-fetches the whole log every 2s while `active` (the run is still in
 * flight) so the timeline streams; stops polling once terminal. (Full-fetch keeps it simple + correct;
 * the `after` cursor is available for a future incremental upgrade on very long runs.)
 */
export function useAgentRunEvents(agentRunId: string | undefined, active: boolean) {
  return useQuery({
    queryKey: ["agent-run-events", agentRunId],
    queryFn: () => agentsApi.listRunEvents(agentRunId!, 0),
    enabled: !!agentRunId,
    refetchInterval: active ? 2000 : false,
  });
}
