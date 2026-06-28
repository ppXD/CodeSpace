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

/** Instantiate a working bench persona by copying a Library store snapshot; invalidates the bench list and the Library (its state may shift). Returns the new id. */
export function useInstantiateAgentFromStore() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (sourceDefinitionId: string) => agentsApi.instantiateAgentFromStore(sourceDefinitionId),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
    ]),
  });
}

/** Replace a persona's editable surface (PUT); invalidates the list + that persona's detail. */
export function useUpdateAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: AgentDefinitionInput }) => agentsApi.updateAgentDefinition(id, input),
    // Return the refetch promise so mutateAsync resolves only once this persona's detail is fresh — the drawer's
    // Edit→Save returns to the inspect view with the new values already loaded (no stale frame), and Save stays
    // in its pending state through the refetch. The PUT returns void, so we re-read rather than seed the cache.
    onSuccess: (_data, { id }) => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["agent", id] }),
      // An imported persona's name/description show in the Library pack detail — keep it in sync.
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
    ]),
  });
}

/** Soft-delete a persona; invalidates the library list + the Library packs (an imported persona belongs to a pack). */
export function useDeleteAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => agentsApi.deleteAgentDefinition(id),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
    ]),
  });
}

/** Full-replace a persona's bound skills; invalidates the list + that persona's detail (its boundSkills changed). */
export function useSetAgentSkills() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, skillIds }: { id: string; skillIds: string[] }) => agentsApi.setAgentSkills(id, skillIds),
    onSuccess: (_data, { id }) => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["agent", id] }),
    ]),
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
 * One agent run's live event log, streamed INCREMENTALLY: each poll (while `active`) fetches only the
 * events past the highest sequence already held (the `after` cursor) and merges them in, so a long run
 * streams deltas instead of re-pulling the whole log every tick. Polling stops once terminal. The log is
 * append-only + immutable, so the merge is a safe dedup-by-sequence (see {@link mergeRunEvents}).
 *
 * `intervalMs` is the live cadence: the expanded terminal streams at 1s, but a wave's collapsed PREVIEW tiles pass a
 * slower cadence (a many-agent wave of M tiles each polling 1s is the steady-state jank driver, and a preview line
 * doesn't need second-by-second freshness). Tiles + terminal share one query per agent, so React Query polls the
 * agent at the fastest cadence among its mounted observers — opening a terminal speeds that one agent back to 1s.
 */
export function useAgentRunEvents(agentRunId: string | undefined, active: boolean, intervalMs = 1000) {
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
    refetchInterval: active ? intervalMs : false,
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
