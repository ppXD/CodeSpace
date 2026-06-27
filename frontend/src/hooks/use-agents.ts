import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { agentsApi, isAgentRunActive, lastEventSequence, mergeRunEvents, type AgentDefinitionInput, type AgentRunEventDto, type ScorecardFilters } from "@/api/agents";

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

/** One persona's full record — the editor's edit-mode load. Keyed by id; only enabled when an id is supplied. */
export function useAgentDefinition(agentDefinitionId: string | undefined) {
  return useQuery({
    queryKey: ["agent", agentDefinitionId],
    queryFn: () => agentsApi.getAgentDefinition(agentDefinitionId!),
    enabled: !!agentDefinitionId,
  });
}

/** Create a persona; invalidates the library list so it reappears on return. Returns the new id. */
export function useCreateAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AgentDefinitionInput) => agentsApi.createAgentDefinition(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["agents"] }),
  });
}

/** Replace a persona's editable surface (PUT); invalidates the list + that persona's detail. */
export function useUpdateAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: AgentDefinitionInput }) => agentsApi.updateAgentDefinition(id, input),
    onSuccess: (_data, { id }) => {
      queryClient.invalidateQueries({ queryKey: ["agents"] });
      queryClient.invalidateQueries({ queryKey: ["agent", id] });
    },
  });
}

/** Soft-delete a persona; invalidates the library list. */
export function useDeleteAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => agentsApi.deleteAgentDefinition(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["agents"] }),
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

/**
 * One agent run's governed tool-call audit — the durable ledger of every side-effecting MCP tool call it
 * made (what tool, the outcome, when, who approved). Unlike the event log this is a small whole-list audit
 * with no incremental cursor, so each tick re-pulls the full list; it polls every ~2s while the run is in
 * flight (a new call lands mid-run) and stops once terminal. Read-only + team-scoped at the source.
 */
export function useToolCalls(agentRunId: string | undefined, active: boolean) {
  return useQuery({
    queryKey: ["agent-run-tool-calls", agentRunId],
    queryFn: () => agentsApi.listToolCalls(agentRunId!),
    enabled: !!agentRunId,
    refetchInterval: active ? 2000 : false,
  });
}

/**
 * The team's agent-run scorecard — per-harness + overall success rate and latency over its terminal runs.
 * Team-scoped at the source (the X-Team-Id header), so it's keyed only by the filters; switching team
 * invalidates the whole cache (see useToolCalls / useAgentDefinitions). A short staleTime keeps an operator's
 * repeated visits cheap without going stale across a working session.
 */
export function useAgentScorecard(filters: ScorecardFilters = {}) {
  return useQuery({
    queryKey: ["agent-scorecard", filters.since ?? null, filters.harness ?? null],
    queryFn: () => agentsApi.getScorecard(filters),
    staleTime: 30 * 1000,
  });
}

/**
 * The team's token + estimated-USD spend roll-up — the cost half of the library measurement strip (success +
 * latency come from {@link useAgentScorecard}). Team-scoped at the source; short staleTime like the scorecard.
 */
export function useTeamCost() {
  return useQuery({
    queryKey: ["agent-cost"],
    queryFn: () => agentsApi.getCost(),
    staleTime: 30 * 1000,
  });
}
