import { useCallback, useEffect, useRef, useState } from "react";

import { ApiError } from "@/api/request";
import { InvalidWorkflowRunToolCallResponseError, workflowsApi, type WorkflowRunToolCallMetadata } from "@/api/workflows";

export const GOVERNED_TOOL_CALL_PAGE_LIMIT = 128;
export const GOVERNED_TOOL_CALL_WINDOW_LIMIT = 512;
export const GOVERNED_TOOL_CALL_POLL_MS = 60_000;
export const GOVERNED_TOOL_CALL_MAX_POLL_MS = 300_000;

export class InvalidWorkflowRunToolCallWindowError extends Error {
  constructor(message = "Invalid Workflow Run tool-call window.") {
    super(message);
    this.name = "InvalidWorkflowRunToolCallWindowError";
  }
}

export interface GovernedToolCallWindow {
  calls: WorkflowRunToolCallMetadata[];
  isLoading: boolean;
  isLoadingOlder: boolean;
  error: Error | null;
  hasOlder: boolean;
  olderCallsOmitted: boolean;
  newerCallsOmitted: boolean;
  atLatest: boolean;
  loadOlder: () => Promise<void>;
  returnToLatest: () => void;
}

interface GovernedToolCallWindowState extends Omit<GovernedToolCallWindow, "loadOlder" | "returnToLatest"> {
  runId: string | undefined;
  nextCursor: string | null;
}

function emptyWindow(runId: string | undefined, isLoading: boolean): GovernedToolCallWindowState {
  return {
    runId,
    calls: [],
    isLoading,
    isLoadingOlder: false,
    error: null,
    hasOlder: false,
    olderCallsOmitted: false,
    newerCallsOmitted: false,
    atLatest: true,
    nextCursor: null,
  };
}

function isAbort(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}

function isTransient(error: unknown): boolean {
  if (error instanceof InvalidWorkflowRunToolCallResponseError || error instanceof InvalidWorkflowRunToolCallWindowError || isAbort(error)) return false;
  if (error instanceof ApiError) return error.status === 408 || error.status === 429 || error.status >= 500;
  return true;
}

function asError(error: unknown, fallback: string): Error {
  return error instanceof Error ? error : new Error(fallback);
}

function requirePage<T>(page: T | null): T {
  if (page === null) throw new InvalidWorkflowRunToolCallWindowError("This Workflow Run's governed tool observations are unavailable.");
  return page;
}

function instantKey(value: string): bigint {
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})$/.exec(value)!;
  return BigInt(Date.parse(`${match[1]}${match[3]}`)) * 1_000_000n + BigInt((match[2] ?? "").padEnd(9, "0"));
}

function hasInvalidOlderPage(existing: readonly WorkflowRunToolCallMetadata[], incoming: readonly WorkflowRunToolCallMetadata[]): boolean {
  const ids = new Set(existing.map(({ toolCallId }) => toolCallId.toLowerCase()));
  if (existing.length > 0 && incoming.length > 0) {
    const boundary = existing.at(-1)!;
    const candidate = incoming[0];
    const boundaryInstant = instantKey(boundary.createdAt);
    const candidateInstant = instantKey(candidate.createdAt);
    if (candidateInstant > boundaryInstant || (candidateInstant === boundaryInstant && candidate.toolCallId.toLowerCase() >= boundary.toolCallId.toLowerCase())) return true;
  }
  return incoming.some(({ toolCallId }) => {
    const id = toolCallId.toLowerCase();
    if (ids.has(id)) return true;
    ids.add(id);
    return false;
  });
}

/** Fixed-size React-local view; neither list pages nor selected attempt detail enter React Query's global cache. */
export function useGovernedToolCallWindow(runId: string | undefined, active: boolean): GovernedToolCallWindow {
  const [state, setState] = useState<GovernedToolCallWindowState>(() => emptyWindow(runId, runId !== undefined));
  const [tailRevision, setTailRevision] = useState(0);
  const [pollRevision, setPollRevision] = useState(0);
  const generationRef = useRef(0);
  const tailControllerRef = useRef<AbortController | null>(null);
  const olderControllerRef = useRef<AbortController | null>(null);
  const pollControllerRef = useRef<AbortController | null>(null);
  const pollingBlockedRef = useRef(false);
  const transientPollFailuresRef = useRef(0);

  useEffect(() => () => {
    ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    pollControllerRef.current?.abort();
  }, []);

  useEffect(() => {
    const generation = ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    pollControllerRef.current?.abort();
    pollingBlockedRef.current = false;
    transientPollFailuresRef.current = 0;
    setState(emptyWindow(runId, runId !== undefined));
    if (runId === undefined) return;

    let retryTimer: number | undefined;
    let failures = 0;
    const loadLatest = () => {
      const controller = new AbortController();
      tailControllerRef.current?.abort();
      tailControllerRef.current = controller;
      void workflowsApi.pageRunToolCalls(runId, { limit: GOVERNED_TOOL_CALL_PAGE_LIMIT }, controller.signal).then((candidate) => {
        const page = requirePage(candidate);
        if (generationRef.current !== generation || controller.signal.aborted) return;
        failures = 0;
        setState({
          runId,
          calls: page.items,
          isLoading: false,
          isLoadingOlder: false,
          error: null,
          hasOlder: page.nextCursor !== null,
          olderCallsOmitted: page.nextCursor !== null,
          newerCallsOmitted: false,
          atLatest: true,
          nextCursor: page.nextCursor,
        });
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        setState((previous) => previous.runId === runId ? { ...previous, error: asError(error, "Could not load governed tool observations.") } : previous);
        if (isTransient(error)) {
          failures = Math.min(failures + 1, 3);
          retryTimer = window.setTimeout(loadLatest, Math.min(GOVERNED_TOOL_CALL_POLL_MS * 2 ** failures, GOVERNED_TOOL_CALL_MAX_POLL_MS));
        } else {
          pollingBlockedRef.current = true;
          setState((previous) => previous.runId === runId ? { ...previous, isLoading: false } : previous);
        }
      });
    };
    loadLatest();

    return () => {
      if (retryTimer !== undefined) window.clearTimeout(retryTimer);
      tailControllerRef.current?.abort();
    };
  }, [runId, tailRevision]);

  const visible = state.runId === runId ? state : emptyWindow(runId, runId !== undefined);

  useEffect(() => {
    if (runId === undefined || !active || visible.isLoading || visible.isLoadingOlder || !visible.atLatest || pollingBlockedRef.current) return;
    const generation = generationRef.current;
    const delay = Math.min(GOVERNED_TOOL_CALL_POLL_MS * 2 ** transientPollFailuresRef.current, GOVERNED_TOOL_CALL_MAX_POLL_MS);
    const timer = window.setTimeout(() => {
      const controller = new AbortController();
      pollControllerRef.current?.abort();
      pollControllerRef.current = controller;
      void workflowsApi.pageRunToolCalls(runId, { limit: GOVERNED_TOOL_CALL_PAGE_LIMIT }, controller.signal).then((candidate) => {
        const page = requirePage(candidate);
        if (generationRef.current !== generation || controller.signal.aborted) return;
        transientPollFailuresRef.current = 0;
        setState({
          runId,
          calls: page.items,
          isLoading: false,
          isLoadingOlder: false,
          error: null,
          hasOlder: page.nextCursor !== null,
          olderCallsOmitted: page.nextCursor !== null,
          newerCallsOmitted: false,
          atLatest: true,
          nextCursor: page.nextCursor,
        });
        setPollRevision((revision) => revision + 1);
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        if (isTransient(error)) {
          transientPollFailuresRef.current = Math.min(transientPollFailuresRef.current + 1, 3);
          setPollRevision((revision) => revision + 1);
        } else pollingBlockedRef.current = true;
        setState((previous) => previous.runId === runId ? { ...previous, error: asError(error, "Could not refresh governed tool observations.") } : previous);
      });
    }, delay);
    return () => {
      window.clearTimeout(timer);
      pollControllerRef.current?.abort();
    };
  }, [active, pollRevision, runId, visible.atLatest, visible.isLoading, visible.isLoadingOlder]);

  const loadOlder = useCallback(async () => {
    if (runId === undefined || visible.isLoading || visible.isLoadingOlder || !visible.hasOlder || visible.nextCursor === null || olderControllerRef.current !== null) return;
    pollControllerRef.current?.abort();
    const generation = generationRef.current;
    const controller = new AbortController();
    olderControllerRef.current = controller;
    setState((previous) => previous.runId === runId ? { ...previous, isLoadingOlder: true, error: null } : previous);
    try {
      const page = requirePage(await workflowsApi.pageRunToolCalls(runId, { cursor: visible.nextCursor, limit: GOVERNED_TOOL_CALL_PAGE_LIMIT }, controller.signal));
      if (generationRef.current !== generation || controller.signal.aborted) return;
      setState((previous) => {
        if (previous.runId !== runId) return previous;
        if (hasInvalidOlderPage(previous.calls, page.items))
          return { ...previous, isLoadingOlder: false, error: new InvalidWorkflowRunToolCallWindowError("An older page contradicted the current governed tool keyset window.") };
        const combined = [...previous.calls, ...page.items];
        const overflow = Math.max(0, combined.length - GOVERNED_TOOL_CALL_WINDOW_LIMIT);
        return {
          ...previous,
          calls: overflow === 0 ? combined : combined.slice(overflow),
          isLoadingOlder: false,
          error: null,
          hasOlder: page.nextCursor !== null,
          olderCallsOmitted: page.nextCursor !== null,
          newerCallsOmitted: previous.newerCallsOmitted || overflow > 0,
          atLatest: false,
          nextCursor: page.nextCursor,
        };
      });
    } catch (error) {
      if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
      setState((previous) => previous.runId === runId ? { ...previous, isLoadingOlder: false, error: asError(error, "Could not load older governed tool observations.") } : previous);
    } finally {
      if (olderControllerRef.current === controller) olderControllerRef.current = null;
    }
  }, [runId, visible.hasOlder, visible.isLoading, visible.isLoadingOlder, visible.nextCursor]);

  const returnToLatest = useCallback(() => {
    ++generationRef.current;
    tailControllerRef.current?.abort();
    olderControllerRef.current?.abort();
    pollControllerRef.current?.abort();
    pollingBlockedRef.current = false;
    transientPollFailuresRef.current = 0;
    setState((previous) => previous.runId === runId ? { ...previous, isLoading: runId !== undefined, isLoadingOlder: false, error: null } : previous);
    setTailRevision((revision) => revision + 1);
  }, [runId]);

  return { ...visible, loadOlder, returnToLatest };
}
