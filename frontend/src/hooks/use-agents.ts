import { useQuery, useQueryClient } from "@tanstack/react-query";

import { agentsApi, isAgentRunActive, lastEventSequence, mergeRunEvents, type AgentRunEventDto } from "@/api/agents";

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
 * One agent run's live event log, streamed INCREMENTALLY: each ~1s poll (while `active`) fetches only the
 * events past the highest sequence already held (the `after` cursor) and merges them in, so a long run
 * streams deltas instead of re-pulling the whole log every tick. Polling stops once terminal. The log is
 * append-only + immutable, so the merge is a safe dedup-by-sequence (see {@link mergeRunEvents}).
 */
export function useAgentRunEvents(agentRunId: string | undefined, active: boolean) {
  const queryClient = useQueryClient();
  const queryKey = ["agent-run-events", agentRunId];

  return useQuery({
    queryKey,
    queryFn: async () => {
      const prev = queryClient.getQueryData<AgentRunEventDto[]>(queryKey) ?? [];
      const fresh = await agentsApi.listRunEvents(agentRunId!, lastEventSequence(prev));
      return mergeRunEvents(prev, fresh);
    },
    enabled: !!agentRunId,
    refetchInterval: active ? 1000 : false,
  });
}
