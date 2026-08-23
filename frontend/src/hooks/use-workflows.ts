import { useCallback, useEffect, useRef, useState } from "react";
import { keepPreviousData, useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";

import { buildRunListParams, InvalidWorkflowRunRecordPageError, workflowsApi, type AnswerDecisionInput, type CreateWorkflowInput, type RunListFilterInput, type RunRecordPageItem, type UpdateWorkflowInput, type WorkflowRunStatus } from "@/api/workflows";

/**
 * Hooks for the workflows engine surface. Same shape as the repository hooks —
 * list/get queries auto-invalidate on mutation, run-status polls every 4 s while a
 * run is non-terminal.
 */

/**
 * Terminal run states — a run that reached one of these never changes again, so the live views
 * stop polling. Everything else (Pending, Enqueued, Running, Suspended) is "active": the run
 * will still transition. Suspended MUST be treated as active — it resumes on its timer /
 * approval / callback, so the run-detail view has to keep refreshing or it freezes the moment a
 * node suspends.
 */
const TERMINAL_RUN_STATUSES = new Set<WorkflowRunStatus>(["Success", "Failure", "Cancelled"]);

/** True while a run can still change state (worth polling). */
export function isRunActive(status: WorkflowRunStatus): boolean {
  return !TERMINAL_RUN_STATUSES.has(status);
}

export function useWorkflows() {
  return useQuery({
    queryKey: ["workflows"],
    queryFn: () => workflowsApi.list(),
  });
}

/** Read one workflow by ref — its GUID (legacy link) or team-unique slug (canonical clean URL). */
export function useWorkflow(ref: string | null) {
  return useQuery({
    queryKey: ["workflow", ref],
    queryFn: () => workflowsApi.get(ref!),
    enabled: ref != null,
  });
}

export function useCreateWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateWorkflowInput) => workflowsApi.create(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}

export function useUpdateWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, input }: { workflowId: string; input: UpdateWorkflowInput }) =>
      workflowsApi.update(workflowId, input),
    onSuccess: (_, { workflowId }) => {
      qc.invalidateQueries({ queryKey: ["workflows"] });
      qc.invalidateQueries({ queryKey: ["workflow", workflowId] });
    },
  });
}

export function useDeleteWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (workflowId: string) => workflowsApi.delete(workflowId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}

export function useSetWorkflowEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, enabled }: { workflowId: string; enabled: boolean }) =>
      workflowsApi.setEnabled(workflowId, enabled),
    onSuccess: (_, { workflowId }) => {
      qc.invalidateQueries({ queryKey: ["workflows"] });
      qc.invalidateQueries({ queryKey: ["workflow", workflowId] });
    },
  });
}

export function useRunWorkflowManually() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, payload }: { workflowId: string; payload?: unknown }) =>
      workflowsApi.runManually(workflowId, payload),
    onSuccess: (_, { workflowId }) => {
      qc.invalidateQueries({ queryKey: ["workflow-runs", workflowId] });
    },
  });
}

export function useWorkflowRuns(workflowId: string | null) {
  return useQuery({
    queryKey: ["workflow-runs", workflowId],
    queryFn: () => workflowsApi.listRuns(workflowId!),
    enabled: workflowId != null,
    // Auto-poll while at least one run is non-terminal — the dispatcher might still be
    // walking it. 4 s feels responsive without hammering. Stops as soon as everything is Success/Failure.
    refetchInterval: (q) => {
      const data = q.state.data;
      if (!data) return false;
      const anyActive = data.some((r) => isRunActive(r.status));
      return anyActive ? 4000 : false;
    },
  });
}

/**
 * The team's runs index (every top-level run, any source). Polls every 4s while at least one run is still
 * non-terminal — the same cadence as the per-workflow run list — and stops once everything has settled.
 */
export function useTeamRuns(filter?: RunListFilterInput, limit = 50, enabled = true) {
  // Key on the canonical serialized filter so two equivalent filters share a cache entry and a changed filter refetches.
  const key = buildRunListParams(filter, limit);
  return useQuery({
    queryKey: ["team-runs", key],
    queryFn: () => workflowsApi.listTeamRuns(filter, limit),
    enabled,
    // Keep the previous page visible while a changed filter refetches, so the bar + board don't blank between filters.
    placeholderData: keepPreviousData,
    // The endpoint is keyset-paginated (RunPage); unwrap to the items array so consumers stay array-based.
    select: (page) => page.items,
    refetchInterval: (q) => {
      const items = q.state.data?.items;
      if (!items) return false;
      return items.some((r) => isRunActive(r.status)) ? 4000 : false;
    },
  });
}

/**
 * The cockpit's TRUE scoped counts for the status cards — counted over the whole scoped set, not a loaded page, so
 * nothing-selected is the genuine superset. `todayStartIso` is the caller's local start-of-day (stable within a day,
 * so the cache key is stable). Polls on the cockpit cadence while anything is live, then settles.
 */
export function useTeamRunSummary(filter: RunListFilterInput | undefined, todayStartIso: string) {
  return useQuery({
    queryKey: ["team-run-summary", buildRunListParams(filter, 1), todayStartIso],
    queryFn: () => workflowsApi.summarizeTeamRuns(filter, todayStartIso),
    placeholderData: keepPreviousData,
    refetchInterval: (q) => ((q.state.data?.live ?? 0) > 0 ? 4000 : false),
  });
}

/**
 * One numbered (offset) page of the team's run HISTORY — the cockpit's paginated past-runs list. Returns the whole
 * RunPage (items + totalCount) so the pager can render "page X of Y" and jump to any page. Deliberately NOT
 * `keepPreviousData`: a page / scope hop must NOT keep the prior rows on screen (they'd read as the new page / scope
 * with no dimming) — a brief honest "Loading…" is correct. `enabled` gates the fetch: the History zone only shows on
 * the default board, so an armed card filter passes false and this stays idle.
 */
export function useTeamRunsHistory(filter: RunListFilterInput | undefined, page: number, pageSize: number, enabled: boolean) {
  const key = buildRunListParams(filter, pageSize, undefined, page);
  return useQuery({
    queryKey: ["team-runs-history", key],
    queryFn: () => workflowsApi.listTeamRunsPage(filter, page, pageSize),
    enabled,
  });
}

/**
 * Phases for a set of LIVE runs, batched — powers the cockpit's Live zone (each run's state sentence) and the
 * "agents active" tally. Shares the per-run ["run-phases", id] cache key with useRunPhases (so a run also open in
 * the Run Room dedups its fetch), polling each every 3s. Results come back aligned with `runIds`.
 */
export function useLiveRunsPhases(runIds: string[]) {
  return useQueries({
    queries: runIds.map((runId) => ({
      queryKey: ["run-phases", runId],
      queryFn: () => workflowsApi.getRunPhases(runId),
      refetchInterval: 3000,
    })),
  });
}

export function useWorkflowRun(runId: string | null) {
  return useQuery({
    queryKey: ["workflow-run", runId],
    queryFn: ({ signal }) => workflowsApi.getRun(runId!, signal),
    enabled: runId != null,
  });
}

/** Explicit full-detail read for raw disclosures. Like the compatibility hook above, it never polls. */
export function useWorkflowRunDetail(runId: string | null, enabled = true) {
  return useQuery({
    queryKey: ["workflow-run", runId],
    queryFn: ({ signal }) => workflowsApi.getRun(runId!, signal),
    enabled: enabled && runId != null,
  });
}

/** Bounded pending-action surface; safe to refresh while the metadata shell remains suspended. */
export function useWorkflowRunPendingWait(runId: string | null, enabled = true) {
  return useQuery({
    queryKey: ["workflow-run-pending-wait", runId],
    queryFn: ({ signal }) => workflowsApi.getRunPendingWait(runId!, signal),
    enabled: enabled && runId != null,
    refetchInterval: enabled ? 2000 : false,
  });
}

export const WORKFLOW_RUN_IDENTITY_POLL_MS = 2000;

/** Bounded canonical route lookup. Full run detail remains owned by the session-less detail fallback. */
export function useWorkflowRunIdentity(ref: string | null, enabled = true) {
  return useQuery({
    queryKey: ["workflow-run-identity", ref],
    queryFn: ({ signal }) => workflowsApi.getRunIdentity(ref!, signal),
    enabled: enabled && ref != null,
    refetchInterval: (query) => {
      const identity = query.state.data;
      return identity && isRunActive(identity.status) ? WORKFLOW_RUN_IDENTITY_POLL_MS : false;
    },
  });
}

/**
 * Observation-only completeness statements. The query is one bounded metadata read; while a run is active it follows
 * newly admitted producer statements, then stops permanently once the parent run is terminal.
 */
export function useRunDataCompleteness(runId: string | null, active = false) {
  return useQuery({
    queryKey: ["run-data-completeness", runId],
    queryFn: ({ signal }) => workflowsApi.getRunDataCompleteness(runId!, signal),
    enabled: runId != null,
    refetchInterval: active ? 4000 : false,
  });
}

/**
 * The lineage's attempt ladder for the run-detail switcher — resolved from any member (the URL run id). Polls while
 * any attempt is still active so a live rerun's status (and a freshly-spawned attempt) stay fresh in the pills.
 */
export function useRunAttempts(runId: string | null) {
  return useQuery({
    queryKey: ["run-attempts", runId],
    queryFn: () => workflowsApi.getRunAttempts(runId!),
    enabled: runId != null,
    refetchInterval: (q) => {
      const data = q.state.data;
      if (!data) return false;
      return data.attempts.some((a) => isRunActive(a.status)) ? 3000 : false;
    },
  });
}

/** One cell's attempt history (every attempt that ran a node/branch) — drives the terminal's per-cell rerun switcher. */
export function useCellAttempts(runId: string | null, nodeId: string | null | undefined, iterationKey: string | null | undefined) {
  return useQuery({
    queryKey: ["cell-attempts", runId, nodeId, iterationKey ?? ""],
    queryFn: () => workflowsApi.getCellAttempts(runId!, nodeId!, iterationKey ?? ""),
    enabled: runId != null && nodeId != null,
  });
}

/**
 * The run's outline — the merged phase tree (the run-neutral projection). Separate query from the run detail
 * (a different endpoint with its own shape), polled on the same 2s cadence while the run is non-terminal so the
 * outline + the canvas/timeline advance in lockstep.
 */
export function useRunPhases(runId: string | null) {
  return useQuery({
    queryKey: ["run-phases", runId],
    queryFn: () => workflowsApi.getRunPhases(runId!),
    enabled: runId != null,
    refetchInterval: (q) => {
      const data = q.state.data;
      if (!data) return false;
      return isRunActive(data.runStatus) ? 2000 : false;
    },
  });
}

/** The run's narrative timeline (the merged event story). Polls every 2s while the run is non-terminal, then stops. */
export function useRunTimeline(runId: string | null) {
  return useQuery({
    queryKey: ["run-timeline", runId],
    queryFn: () => workflowsApi.getRunTimeline(runId!),
    enabled: runId != null,
    refetchInterval: (q) => {
      const data = q.state.data;
      if (!data) return false;
      return isRunActive(data.runStatus) ? 2000 : false;
    },
  });
}

/** Legacy full-ledger compatibility reader. Trace uses useRunRecordWindow; this endpoint is never auto-polled. */
export function useRunRecords(runId: string | null) {
  return useQuery({
    queryKey: ["run-records", runId],
    queryFn: () => workflowsApi.getRunRecords(runId!),
    enabled: runId != null,
  });
}

export const RUN_RECORD_PAGE_LIMIT = 128;
export const RUN_RECORD_WINDOW_LIMIT = 512;
export const RUN_RECORD_WINDOW_POLL_MS = 2000;
export const RUN_RECORD_WINDOW_MAX_POLL_MS = 16000;

export interface RunRecordWindow {
  records: RunRecordPageItem[];
  runStatus: WorkflowRunStatus | undefined;
  isLoading: boolean;
  isLoadingOlder: boolean;
  error: Error | null;
  hasOlder: boolean;
  olderRecordsOmitted: boolean;
  newerRecordsOmitted: boolean;
  atLatest: boolean;
  loadOlder: () => Promise<void>;
  returnToLatest: () => void;
}

interface RunRecordWindowState extends Omit<RunRecordWindow, "loadOlder" | "returnToLatest"> {
  runId: string | null;
}

function emptyRunRecordWindow(runId: string | null, isLoading: boolean): RunRecordWindowState {
  return {
    runId,
    records: [],
    runStatus: undefined,
    isLoading,
    isLoadingOlder: false,
    error: null,
    hasOlder: false,
    olderRecordsOmitted: false,
    newerRecordsOmitted: false,
    atLatest: true,
  };
}

function isAbort(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}

/**
 * A fixed-size local window over the raw ledger. Pages deliberately bypass React Query: no body page is retained in
 * the global cache, and both the component state and rendered DOM are capped at RUN_RECORD_WINDOW_LIMIT records.
 */
export function useRunRecordWindow(runId: string | null): RunRecordWindow {
  const [state, setState] = useState<RunRecordWindowState>(() => emptyRunRecordWindow(runId, runId != null));
  const [tailRevision, setTailRevision] = useState(0);
  const [pollRevision, setPollRevision] = useState(0);
  const generationRef = useRef(0);
  const tailControllerRef = useRef<AbortController | null>(null);
  const olderControllerRef = useRef<AbortController | null>(null);
  const newerControllerRef = useRef<AbortController | null>(null);
  const pollingBlockedRef = useRef(false);
  const transientPollFailuresRef = useRef(0);
  const transientTailFailuresRef = useRef(0);
  const tailRunIdRef = useRef(runId);

  useEffect(() => {
    if (tailRunIdRef.current !== runId) {
      tailRunIdRef.current = runId;
      transientTailFailuresRef.current = 0;
    }
    const generation = ++generationRef.current;
    let retryTimer: number | null = null;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    newerControllerRef.current?.abort();
    pollingBlockedRef.current = false;
    transientPollFailuresRef.current = 0;
    setState(emptyRunRecordWindow(runId, runId != null));
    if (runId == null) return;

    const controller = new AbortController();
    tailControllerRef.current = controller;
    void workflowsApi.getRunRecordPage(runId, { limit: RUN_RECORD_PAGE_LIMIT }, controller.signal).then((page) => {
      if (generationRef.current !== generation || controller.signal.aborted) return;
      transientTailFailuresRef.current = 0;
      setState({
        runId,
        records: page.records,
        runStatus: page.runStatus,
        isLoading: false,
        isLoadingOlder: false,
        error: null,
        hasOlder: page.nextBeforeSequence !== null,
        olderRecordsOmitted: page.nextBeforeSequence !== null,
        newerRecordsOmitted: false,
        atLatest: true,
      });
    }).catch((error: unknown) => {
      if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
      setState({ ...emptyRunRecordWindow(runId, false), error: error instanceof Error ? error : new Error("Could not load the Workflow Run record window.") });
      if (error instanceof InvalidWorkflowRunRecordPageError) return;
      transientTailFailuresRef.current = Math.min(transientTailFailuresRef.current + 1, 4);
      const delay = Math.min(RUN_RECORD_WINDOW_POLL_MS * 2 ** (transientTailFailuresRef.current - 1), RUN_RECORD_WINDOW_MAX_POLL_MS);
      retryTimer = window.setTimeout(() => {
        if (generationRef.current === generation && !controller.signal.aborted) setTailRevision((revision) => revision + 1);
      }, delay);
    });

    return () => {
      if (retryTimer !== null) window.clearTimeout(retryTimer);
      controller.abort();
      if (tailControllerRef.current === controller) tailControllerRef.current = null;
    };
  }, [runId, tailRevision]);

  const visible = state.runId === runId ? state : emptyRunRecordWindow(runId, runId != null);
  const newestSequence = visible.records.at(-1)?.sequence ?? 0;

  useEffect(() => {
    if (runId == null || visible.isLoading || visible.isLoadingOlder || !visible.atLatest || visible.runStatus == null || !isRunActive(visible.runStatus) || pollingBlockedRef.current) return;

    const generation = generationRef.current;
    const delay = Math.min(RUN_RECORD_WINDOW_POLL_MS * 2 ** transientPollFailuresRef.current, RUN_RECORD_WINDOW_MAX_POLL_MS);
    const timer = window.setTimeout(() => {
      const controller = new AbortController();
      newerControllerRef.current?.abort();
      newerControllerRef.current = controller;
      void workflowsApi.getRunRecordPage(runId, { afterSequence: newestSequence, limit: RUN_RECORD_PAGE_LIMIT }, controller.signal).then((page) => {
        if (generationRef.current !== generation || controller.signal.aborted) return;
        transientPollFailuresRef.current = 0;
        setState((previous) => {
          if (previous.runId !== runId) return previous;
          const combined = [...previous.records, ...page.records];
          const overflow = Math.max(0, combined.length - RUN_RECORD_WINDOW_LIMIT);
          const terminalBacklog = !isRunActive(page.runStatus) && page.nextAfterSequence !== null;
          return {
            ...previous,
            records: overflow === 0 ? combined : combined.slice(overflow),
            runStatus: page.runStatus,
            error: null,
            hasOlder: previous.hasOlder || overflow > 0,
            olderRecordsOmitted: previous.olderRecordsOmitted || overflow > 0,
            newerRecordsOmitted: previous.newerRecordsOmitted || terminalBacklog,
            atLatest: previous.atLatest && !terminalBacklog,
          };
        });
        setPollRevision((revision) => revision + 1);
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        if (error instanceof InvalidWorkflowRunRecordPageError) pollingBlockedRef.current = true;
        else transientPollFailuresRef.current = Math.min(transientPollFailuresRef.current + 1, 3);
        setState((previous) => previous.runId === runId ? { ...previous, error: error instanceof Error ? error : new Error("Could not refresh the Workflow Run record window.") } : previous);
        if (!(error instanceof InvalidWorkflowRunRecordPageError)) setPollRevision((revision) => revision + 1);
      }).finally(() => {
        if (newerControllerRef.current === controller) newerControllerRef.current = null;
      });
    }, delay);

    return () => {
      window.clearTimeout(timer);
      newerControllerRef.current?.abort();
      newerControllerRef.current = null;
    };
  }, [newestSequence, pollRevision, runId, visible.atLatest, visible.isLoading, visible.isLoadingOlder, visible.runStatus]);

  const loadOlder = useCallback(async () => {
    if (runId == null || visible.isLoading || visible.isLoadingOlder || !visible.hasOlder || visible.records.length === 0 || olderControllerRef.current !== null) return;
    newerControllerRef.current?.abort();
    newerControllerRef.current = null;
    const generation = generationRef.current;
    const beforeSequence = visible.records[0].sequence;
    const controller = new AbortController();
    olderControllerRef.current = controller;
    setState((previous) => previous.runId === runId ? { ...previous, isLoadingOlder: true, error: null } : previous);

    try {
      const page = await workflowsApi.getRunRecordPage(runId, { beforeSequence, limit: RUN_RECORD_PAGE_LIMIT }, controller.signal);
      if (generationRef.current !== generation || controller.signal.aborted) return;
      setState((previous) => {
        if (previous.runId !== runId) return previous;
        const combined = [...page.records, ...previous.records];
        const overflow = Math.max(0, combined.length - RUN_RECORD_WINDOW_LIMIT);
        return {
          ...previous,
          records: overflow === 0 ? combined : combined.slice(0, RUN_RECORD_WINDOW_LIMIT),
          runStatus: page.runStatus,
          isLoadingOlder: false,
          error: null,
          hasOlder: page.nextBeforeSequence !== null,
          olderRecordsOmitted: page.nextBeforeSequence !== null,
          newerRecordsOmitted: previous.newerRecordsOmitted || overflow > 0,
          atLatest: previous.atLatest && overflow === 0,
        };
      });
    } catch (error) {
      if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
      setState((previous) => previous.runId === runId ? { ...previous, isLoadingOlder: false, error: error instanceof Error ? error : new Error("Could not load older Workflow Run records.") } : previous);
    } finally {
      if (olderControllerRef.current === controller) olderControllerRef.current = null;
    }
  }, [runId, visible.hasOlder, visible.isLoading, visible.isLoadingOlder, visible.records]);

  const returnToLatest = useCallback(() => {
    ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    newerControllerRef.current?.abort();
    pollingBlockedRef.current = false;
    transientPollFailuresRef.current = 0;
    transientTailFailuresRef.current = 0;
    setState((previous) => previous.runId === runId ? { ...previous, isLoading: runId != null, isLoadingOlder: false, error: null } : previous);
    setTailRevision((revision) => revision + 1);
  }, [runId]);

  return { ...visible, loadOlder, returnToLatest };
}

/**
 * The team's cross-grain "Needs decision" queue. The Run Room filters this to the run's tree (by `rootTraceId`)
 * client-side — a per-run endpoint is a future backend filter. Polled every 3s while `poll` is true (the run is
 * still live and can park a fresh decision); a terminal run has none outstanding, so the caller stops it.
 */
export function usePendingDecisions(poll = true) {
  return useQuery({
    queryKey: ["pending-decisions"],
    queryFn: () => workflowsApi.listPendingDecisions(),
    refetchInterval: poll ? 3000 : false,
  });
}

/**
 * Answer a pending decision (either grain). Resolving it resumes the parked run. Invalidates on SETTLED, not just
 * success: an `AlreadyResolved` (another answerer / the deadline won) throws, and we still want the queue to refetch
 * so the now-stale card drops — only a genuine `Invalid` answer is surfaced inline by the card.
 */
export function useAnswerDecision() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ decisionId, body }: { decisionId: string; body: AnswerDecisionInput }) =>
      workflowsApi.answerDecision(decisionId, body),
    onSettled: () => {
      qc.invalidateQueries({ queryKey: ["pending-decisions"] });
      qc.invalidateQueries({ queryKey: ["run-phases"] });
      qc.invalidateQueries({ queryKey: ["workflow-run"] });
    },
  });
}

export function useReplayRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (runId: string) => workflowsApi.replayRun(runId),
    onSuccess: (_, originalRunId) => {
      // The original run hasn't changed, but its workflow's run list has a new entry.
      // We don't know the workflowId here without an extra query, so invalidate broadly.
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["workflow-run", originalRunId] });
    },
  });
}

/** The Room's "Open PR" action (PR-6) — opens, or reuses, a PR for a terminal run's published branch(es). */
export function useOpenPullRequest(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => workflowsApi.openPullRequest(runId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
    },
  });
}

/**
 * Re-run ONE fanned-out item of a top-level flow.map — forks a fresh run that reuses the sibling items. Returns
 * the new run id (the caller navigates to it). The `operationId` makes a double-submit idempotent; a concurrent
 * rerun of the same item is refused 409 by the active-rerun lease.
 */
export function useRerunMapBranch(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { mapNodeId: string; branchIndex: number; operationId: string }) => workflowsApi.rerunMapBranch(runId, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
      qc.invalidateQueries({ queryKey: ["team-runs"] });
    },
  });
}

/** Re-run a SET of a top-level flow.map's items ("Rerun all failed items") in ONE fork. Same contract as useRerunMapBranch. */
export function useRerunMapBranches(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { mapNodeId: string; branchIndices: number[]; operationId: string }) => workflowsApi.rerunMapBranches(runId, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
      qc.invalidateQueries({ queryKey: ["team-runs"] });
    },
  });
}

/** Re-run FROM a node ("Rerun from here") — forks a run reusing everything upstream and re-running this node + its downstream. Returns the new run id. */
export function useRerunFromNode(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { fromNodeId: string }) => workflowsApi.rerunFromNode(runId, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
      qc.invalidateQueries({ queryKey: ["team-runs"] });
    },
  });
}

/**
 * Cancel (hard-stop) a still-live run — the operator action. Invalidates the run's views so the terminal
 * Cancelled state shows at once (the 2s status poll would also catch it). The backend POST /cancel is idempotent.
 */
export function useCancelRun(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => workflowsApi.cancelRun(runId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
      qc.invalidateQueries({ queryKey: ["run-phases", runId] });
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["team-runs"] });
    },
  });
}

/** Approve / reject a run parked on a flow.wait_approval, then resume it. */
export function useResumeRun(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { approved: boolean; comment?: string }) => workflowsApi.resumeRun(runId, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
    },
  });
}

/** Continue a stranded Suspended run (Suspended with no pending wait) on demand — drives the same re-dispatch the reconciler does. */
export function useContinueRun(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => workflowsApi.continueRun(runId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["workflow-run", runId] });
      qc.invalidateQueries({ queryKey: ["run-phases", runId] });
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["team-runs"] });
    },
  });
}

export function useNodeManifests() {
  return useQuery({
    queryKey: ["node-manifests"],
    queryFn: () => workflowsApi.listNodeManifests(),
    staleTime: Infinity,   // Manifests don't change while the page is open.
  });
}

/**
 * Engine-injected sys.* variables (key + type + one-line description). Static per release,
 * so we cache with `staleTime: Infinity` — same lifetime model as node manifests. Replaces
 * the old hardcoded SYSTEM_VARIABLES constant; the canonical list now lives on the backend
 * (SystemScopeKeys.Descriptors) and adding a new key requires no frontend change.
 */
export function useSystemVariables() {
  return useQuery({
    queryKey: ["system-variables"],
    queryFn: () => workflowsApi.listSystemVariables(),
    staleTime: Infinity,
  });
}
