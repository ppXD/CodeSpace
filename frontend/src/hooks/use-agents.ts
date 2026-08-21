import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState } from "react";

import { agentsApi, InvalidAgentRunEventPageError, isAgentRunActive, lastEventSequence, mergeRunEvents, type AgentDefinitionInput, type AgentRunEventDto, type ScorecardFilters } from "@/api/agents";
import { ApiError } from "@/api/request";

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
    // Instantiate creates a WORKING bench copy (PackId null) — it doesn't touch any pack's Store artifacts, so the
    // [pack-artifacts] detail lists stay valid; only the bench + the Library's surfacing state can shift.
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
    ]),
  });
}

/** Author a new agent INTO the Library (a Custom-pack store entry, off the bench); invalidates the Library packs/detail. Returns the new id. */
export function useAuthorStoreAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; description?: string | null; systemPrompt?: string | null }) => agentsApi.authorStoreAgent(input),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
      queryClient.invalidateQueries({ queryKey: ["pack-artifacts"] }),
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
      queryClient.invalidateQueries({ queryKey: ["pack-artifacts"] }),
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
      queryClient.invalidateQueries({ queryKey: ["pack-artifacts"] }),
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

export const AGENT_EVENT_PAGE_LIMIT = 128;
export const AGENT_EVENT_WINDOW_LIMIT = 512;
export const AGENT_EVENT_WINDOW_POLL_MS = 1000;
export const AGENT_EVENT_WINDOW_MAX_POLL_MS = 8000;

export interface AgentRunEventWindow {
  data: AgentRunEventDto[];
  isLoading: boolean;
  isLoadingOlder: boolean;
  error: Error | null;
  hasOlder: boolean;
  olderEventsOmitted: boolean;
  newerEventsOmitted: boolean;
  atLatest: boolean;
  loadOlder: () => Promise<void>;
  returnToLatest: () => void;
}

interface AgentRunEventWindowState extends Omit<AgentRunEventWindow, "loadOlder" | "returnToLatest"> {
  agentRunId: string | undefined;
  nextOlderCursor: string | null;
  nextNewerCursor: string;
}

function emptyAgentRunEventWindow(agentRunId: string | undefined, isLoading: boolean): AgentRunEventWindowState {
  return {
    agentRunId,
    data: [],
    isLoading,
    isLoadingOlder: false,
    error: null,
    hasOlder: false,
    olderEventsOmitted: false,
    newerEventsOmitted: false,
    atLatest: true,
    nextOlderCursor: null,
    nextNewerCursor: "0",
  };
}

function isAbort(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}

function isTransientEventPageError(error: unknown): boolean {
  if (error instanceof InvalidAgentRunEventPageError || isAbort(error)) return false;
  if (error instanceof ApiError) return error.status === 408 || error.status === 429 || error.status >= 500;
  return true;
}

function asEventPageError(error: unknown, fallback: string): Error {
  return error instanceof Error ? error : new Error(fallback);
}

/**
 * A fixed-size, React-local window over one Agent Run's governed event stream. The legacy event query remains for
 * compact preview consumers, while the terminal uses this page reader so neither the DOM nor React Query cache grows
 * with a long-running CLI transcript.
 */
export function useAgentRunEventWindow(agentRunId: string | undefined, active: boolean): AgentRunEventWindow {
  const [state, setState] = useState<AgentRunEventWindowState>(() => emptyAgentRunEventWindow(agentRunId, agentRunId !== undefined));
  const [tailRevision, setTailRevision] = useState(0);
  const [pollRevision, setPollRevision] = useState(0);
  const generationRef = useRef(0);
  const tailControllerRef = useRef<AbortController | null>(null);
  const olderControllerRef = useRef<AbortController | null>(null);
  const newerControllerRef = useRef<AbortController | null>(null);
  const pollingBlockedRef = useRef(false);
  const transientPollFailuresRef = useRef(0);

  useEffect(() => () => {
    ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    newerControllerRef.current?.abort();
    tailControllerRef.current = null;
    olderControllerRef.current = null;
    newerControllerRef.current = null;
  }, []);

  useEffect(() => {
    const generation = ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    newerControllerRef.current?.abort();
    pollingBlockedRef.current = false;
    transientPollFailuresRef.current = 0;
    setState(emptyAgentRunEventWindow(agentRunId, agentRunId !== undefined));
    if (agentRunId === undefined) return;

    let retryTimer: number | undefined;
    let failures = 0;
    const loadTail = () => {
      const controller = new AbortController();
      tailControllerRef.current?.abort();
      tailControllerRef.current = controller;
      void agentsApi.pageRunEvents(agentRunId, { mode: "Tail", limit: AGENT_EVENT_PAGE_LIMIT }, controller.signal).then((page) => {
        if (generationRef.current !== generation || controller.signal.aborted) return;
        setState({
          agentRunId,
          data: page.items,
          isLoading: false,
          isLoadingOlder: false,
          error: null,
          hasOlder: page.hasOlder,
          olderEventsOmitted: page.hasOlder,
          newerEventsOmitted: false,
          atLatest: true,
          nextOlderCursor: page.nextOlderCursor,
          nextNewerCursor: page.nextNewerCursor,
        });
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        failures = Math.min(failures + 1, 3);
        setState((previous) => previous.agentRunId === agentRunId ? { ...previous, error: asEventPageError(error, "Could not load the Agent Run event window.") } : previous);
        if (isTransientEventPageError(error)) {
          const delay = Math.min(AGENT_EVENT_WINDOW_POLL_MS * 2 ** failures, AGENT_EVENT_WINDOW_MAX_POLL_MS);
          retryTimer = window.setTimeout(loadTail, delay);
        } else {
          setState((previous) => previous.agentRunId === agentRunId ? { ...previous, isLoading: false } : previous);
        }
      });
    };
    loadTail();

    return () => {
      if (retryTimer !== undefined) window.clearTimeout(retryTimer);
      tailControllerRef.current?.abort();
      if (tailControllerRef.current?.signal.aborted) tailControllerRef.current = null;
    };
  }, [agentRunId, tailRevision]);

  const visible = state.agentRunId === agentRunId ? state : emptyAgentRunEventWindow(agentRunId, agentRunId !== undefined);

  useEffect(() => {
    if (agentRunId === undefined || !active || visible.isLoading || visible.isLoadingOlder || !visible.atLatest || pollingBlockedRef.current) return;

    const generation = generationRef.current;
    const delay = Math.min(AGENT_EVENT_WINDOW_POLL_MS * 2 ** transientPollFailuresRef.current, AGENT_EVENT_WINDOW_MAX_POLL_MS);
    const timer = window.setTimeout(() => {
      const controller = new AbortController();
      newerControllerRef.current?.abort();
      newerControllerRef.current = controller;
      void agentsApi.pageRunEvents(agentRunId, { mode: "Newer", cursor: visible.nextNewerCursor, limit: AGENT_EVENT_PAGE_LIMIT }, controller.signal).then((page) => {
        if (generationRef.current !== generation || controller.signal.aborted) return;
        transientPollFailuresRef.current = 0;
        setState((previous) => {
          if (previous.agentRunId !== agentRunId) return previous;
          const combined = [...previous.data, ...page.items];
          const overflow = Math.max(0, combined.length - AGENT_EVENT_WINDOW_LIMIT);
          return {
            ...previous,
            data: overflow === 0 ? combined : combined.slice(overflow),
            error: null,
            // Newer.hasOlder only says the server has rows at/before this query cursor; those rows are already in
            // our window. Only a pre-existing gap or a local cap eviction proves that earlier rows are omitted.
            hasOlder: previous.hasOlder || overflow > 0,
            olderEventsOmitted: previous.olderEventsOmitted || overflow > 0,
            newerEventsOmitted: page.hasNewer,
            nextOlderCursor: overflow > 0 ? String(combined[overflow].sequence) : previous.nextOlderCursor,
            nextNewerCursor: page.nextNewerCursor,
          };
        });
        setPollRevision((revision) => revision + 1);
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        if (error instanceof InvalidAgentRunEventPageError || !isTransientEventPageError(error)) pollingBlockedRef.current = true;
        else transientPollFailuresRef.current = Math.min(transientPollFailuresRef.current + 1, 3);
        setState((previous) => previous.agentRunId === agentRunId ? { ...previous, error: asEventPageError(error, "Could not refresh the Agent Run event window.") } : previous);
        if (isTransientEventPageError(error)) setPollRevision((revision) => revision + 1);
      }).finally(() => {
        if (newerControllerRef.current === controller) newerControllerRef.current = null;
      });
    }, delay);

    return () => {
      window.clearTimeout(timer);
      newerControllerRef.current?.abort();
      newerControllerRef.current = null;
    };
  }, [active, agentRunId, pollRevision, visible.atLatest, visible.isLoading, visible.isLoadingOlder, visible.nextNewerCursor]);

  const loadOlder = useCallback(async () => {
    if (agentRunId === undefined || visible.isLoading || visible.isLoadingOlder || !visible.hasOlder || visible.nextOlderCursor === null || olderControllerRef.current !== null) return;
    newerControllerRef.current?.abort();
    newerControllerRef.current = null;
    const generation = generationRef.current;
    const controller = new AbortController();
    olderControllerRef.current = controller;
    setState((previous) => previous.agentRunId === agentRunId ? { ...previous, isLoadingOlder: true, error: null } : previous);

    try {
      const page = await agentsApi.pageRunEvents(agentRunId, { mode: "Older", cursor: visible.nextOlderCursor, limit: AGENT_EVENT_PAGE_LIMIT }, controller.signal);
      if (generationRef.current !== generation || controller.signal.aborted) return;
      setState((previous) => {
        if (previous.agentRunId !== agentRunId) return previous;
        const combined = [...page.items, ...previous.data];
        const overflow = Math.max(0, combined.length - AGENT_EVENT_WINDOW_LIMIT);
        return {
          ...previous,
          data: overflow === 0 ? combined : combined.slice(0, AGENT_EVENT_WINDOW_LIMIT),
          isLoadingOlder: false,
          error: null,
          hasOlder: page.hasOlder,
          olderEventsOmitted: page.hasOlder,
          newerEventsOmitted: previous.newerEventsOmitted || overflow > 0,
          atLatest: previous.atLatest && overflow === 0,
          nextOlderCursor: page.nextOlderCursor,
        };
      });
    } catch (error) {
      if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
      setState((previous) => previous.agentRunId === agentRunId ? { ...previous, isLoadingOlder: false, error: asEventPageError(error, "Could not load older Agent Run events.") } : previous);
    } finally {
      if (olderControllerRef.current === controller) olderControllerRef.current = null;
    }
  }, [agentRunId, visible.hasOlder, visible.isLoading, visible.isLoadingOlder, visible.nextOlderCursor]);

  const returnToLatest = useCallback(() => {
    ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    newerControllerRef.current?.abort();
    pollingBlockedRef.current = false;
    transientPollFailuresRef.current = 0;
    setState((previous) => previous.agentRunId === agentRunId ? { ...previous, isLoading: agentRunId !== undefined, isLoadingOlder: false, error: null } : previous);
    setTailRevision((revision) => revision + 1);
  }, [agentRunId]);

  return { ...visible, loadOlder, returnToLatest };
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

/**
 * Per-agent run stats — each persona's recent-outcome sparkline + windowed success / latency / spend, joined onto
 * the roster by agentDefinitionId. Team-scoped at the source; keyed on the `since` window so changing the roster's
 * time control refetches. Short staleTime like the scorecard, so repeated visits within a session stay cheap.
 */
export function useAgentStats(since?: string) {
  return useQuery({
    queryKey: ["agent-stats", since ?? null],
    queryFn: () => agentsApi.getStats(since),
    staleTime: 30 * 1000,
    // Keep the prior window's rows on screen while a new window fetches, so toggling 7d/30d/all never flashes the
    // roster back to "no runs" empty states (isLoading stays true only on the very first load).
    placeholderData: keepPreviousData,
  });
}
