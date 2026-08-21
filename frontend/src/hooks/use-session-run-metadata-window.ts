import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { ApiError } from "@/api/request";
import { InvalidSessionRunMetadataPageError, sessionsApi, type SessionRunMetadataItem, type SessionRunMetadataPage, type SessionRunMetadataSelector } from "@/api/sessions";

export const SESSION_RUN_METADATA_PAGE_LIMIT = 128;
export const SESSION_RUN_METADATA_WINDOW_LIMIT = 512;
export const SESSION_RUN_METADATA_POLL_MS = 2_000;
const MAX_TRANSIENT_FAILURES = 3;
const MAX_POLL_DELAY_MS = 8_000;

export interface SessionRunMetadataWindow {
  items: SessionRunMetadataItem[];
  isLoading: boolean;
  isLoadingOlder: boolean;
  error: Error | null;
  olderOmitted: boolean;
  newerOmitted: boolean;
  atLatest: boolean;
  consistency: "MembershipHeadOnly";
  loadOlder: () => Promise<void>;
  returnToLatest: () => void;
}

interface WindowState extends Omit<SessionRunMetadataWindow, "loadOlder" | "returnToLatest"> {
  selectorKey: string | null;
  membershipHeadRunNumber: number | null;
  olderCursor: string | null;
}

function emptyState(selectorKey: string | null): WindowState {
  return {
    selectorKey,
    membershipHeadRunNumber: null,
    olderCursor: null,
    items: [],
    isLoading: selectorKey !== null,
    isLoadingOlder: false,
    error: null,
    olderOmitted: false,
    newerOmitted: false,
    atLatest: true,
    consistency: "MembershipHeadOnly",
  };
}

function selectorIdentity(selector: SessionRunMetadataSelector | undefined): string | null {
  if (selector === undefined) return null;
  return selector.kind === "Session" ? `session:${selector.sessionId}` : `run:${selector.runAnchorId}`;
}

function asError(error: unknown, fallback: string): Error { return error instanceof Error ? error : new Error(fallback); }
function isAbort(error: unknown): boolean { return error instanceof Error && error.name === "AbortError"; }
function transient(error: unknown): boolean {
  if (isAbort(error) || error instanceof InvalidSessionRunMetadataPageError) return false;
  if (error instanceof ApiError) return error.status === 408 || error.status === 429 || error.status >= 500;
  return true;
}

function fromTail(selectorKey: string, page: SessionRunMetadataPage): WindowState {
  return {
    selectorKey,
    membershipHeadRunNumber: page.membershipHeadRunNumber,
    olderCursor: page.continuation.olderCursor,
    items: page.items,
    isLoading: false,
    isLoadingOlder: false,
    error: null,
    olderOmitted: page.omitted.older,
    newerOmitted: false,
    atLatest: true,
    consistency: "MembershipHeadOnly",
  };
}

/**
 * React-local bounded window. The page's head freezes membership only; status/error/timing remain fresh observations.
 * No component mounts this foundation yet, so introducing it adds zero production requests until a later cutover.
 */
export function useSessionRunMetadataWindow(selector: SessionRunMetadataSelector | undefined, active: boolean): SessionRunMetadataWindow {
  const selectorKey = selectorIdentity(selector);
  const selectorKind = selector?.kind;
  const selectorSessionId = selector?.sessionId;
  const selectorRunAnchorId = selector?.runAnchorId;
  const stableSelector = useMemo<SessionRunMetadataSelector | undefined>(() => selectorKind === "Session"
    ? { kind: "Session", sessionId: selectorSessionId!, runAnchorId: null }
    : selectorKind === "RunAnchor" ? { kind: "RunAnchor", sessionId: null, runAnchorId: selectorRunAnchorId! } : undefined,
  [selectorKind, selectorRunAnchorId, selectorSessionId]);
  const [state, setState] = useState<WindowState>(() => emptyState(selectorKey));
  const [tailRevision, setTailRevision] = useState(0);
  const [pollRevision, setPollRevision] = useState(0);
  const generationRef = useRef(0);
  const tailControllerRef = useRef<AbortController | null>(null);
  const olderControllerRef = useRef<AbortController | null>(null);
  const pollControllerRef = useRef<AbortController | null>(null);
  const pollFailuresRef = useRef(0);
  const pollingBlockedRef = useRef(false);

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
    pollFailuresRef.current = 0;
    pollingBlockedRef.current = false;
    setState(emptyState(selectorKey));
    if (stableSelector === undefined || selectorKey === null) return;

    let failures = 0;
    let retryTimer: number | undefined;
    const load = () => {
      const controller = new AbortController();
      tailControllerRef.current?.abort();
      tailControllerRef.current = controller;
      void sessionsApi.pageRunMetadata(stableSelector, { direction: "Tail", limit: SESSION_RUN_METADATA_PAGE_LIMIT }, controller.signal).then((page) => {
        if (generationRef.current !== generation || controller.signal.aborted) return;
        setState(fromTail(selectorKey, page));
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        failures += 1;
        setState((previous) => previous.selectorKey === selectorKey ? { ...previous, isLoading: failures < MAX_TRANSIENT_FAILURES && transient(error), error: asError(error, "Could not load Session run metadata.") } : previous);
        if (transient(error) && failures < MAX_TRANSIENT_FAILURES) {
          retryTimer = window.setTimeout(load, Math.min(SESSION_RUN_METADATA_POLL_MS * 2 ** failures, MAX_POLL_DELAY_MS));
        } else {
          pollingBlockedRef.current = true;
        }
      }).finally(() => {
        if (tailControllerRef.current === controller) tailControllerRef.current = null;
      });
    };
    load();

    return () => {
      if (retryTimer !== undefined) window.clearTimeout(retryTimer);
      tailControllerRef.current?.abort();
      tailControllerRef.current = null;
    };
  }, [selectorKey, stableSelector, tailRevision]);

  const visible = state.selectorKey === selectorKey ? state : emptyState(selectorKey);

  useEffect(() => {
    if (stableSelector === undefined || selectorKey === null || !active || visible.isLoading || visible.isLoadingOlder || !visible.atLatest || pollingBlockedRef.current) return;
    const generation = generationRef.current;
    const timer = window.setTimeout(() => {
      const controller = new AbortController();
      pollControllerRef.current?.abort();
      pollControllerRef.current = controller;
      void sessionsApi.pageRunMetadata(stableSelector, { direction: "Tail", limit: SESSION_RUN_METADATA_PAGE_LIMIT }, controller.signal).then((page) => {
        if (generationRef.current !== generation || controller.signal.aborted) return;
        pollFailuresRef.current = 0;
        setState(fromTail(selectorKey, page));
        setPollRevision((revision) => revision + 1);
      }).catch((error: unknown) => {
        if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
        const canRetry = transient(error) && ++pollFailuresRef.current < MAX_TRANSIENT_FAILURES;
        if (!canRetry) pollingBlockedRef.current = true;
        setState((previous) => previous.selectorKey === selectorKey ? { ...previous, error: asError(error, "Could not refresh Session run metadata.") } : previous);
        if (canRetry) setPollRevision((revision) => revision + 1);
      }).finally(() => {
        if (pollControllerRef.current === controller) pollControllerRef.current = null;
      });
    }, Math.min(SESSION_RUN_METADATA_POLL_MS * 2 ** pollFailuresRef.current, MAX_POLL_DELAY_MS));

    return () => {
      window.clearTimeout(timer);
      pollControllerRef.current?.abort();
      pollControllerRef.current = null;
    };
  }, [active, pollRevision, selectorKey, stableSelector, visible.atLatest, visible.isLoading, visible.isLoadingOlder]);

  const loadOlder = useCallback(async () => {
    if (stableSelector === undefined || selectorKey === null || visible.isLoading || visible.isLoadingOlder || !visible.olderOmitted
      || visible.olderCursor === null || visible.membershipHeadRunNumber === null || olderControllerRef.current !== null) return;
    pollControllerRef.current?.abort();
    pollControllerRef.current = null;
    const generation = generationRef.current;
    const controller = new AbortController();
    olderControllerRef.current = controller;
    setState((previous) => previous.selectorKey === selectorKey ? { ...previous, isLoadingOlder: true, error: null } : previous);

    try {
      const page = await sessionsApi.pageRunMetadata(stableSelector, {
        direction: "Older",
        cursor: visible.olderCursor,
        membershipHeadRunNumber: visible.membershipHeadRunNumber,
        limit: SESSION_RUN_METADATA_PAGE_LIMIT,
      }, controller.signal);
      if (generationRef.current !== generation || controller.signal.aborted) return;
      setState((previous) => {
        if (previous.selectorKey !== selectorKey) return previous;
        if (page.items.length > 0 && previous.items.length > 0 && page.items.at(-1)!.runNumber >= previous.items[0].runNumber)
          return { ...previous, isLoadingOlder: false, error: new InvalidSessionRunMetadataPageError() };
        const combined = [...page.items, ...previous.items];
        const overflow = Math.max(0, combined.length - SESSION_RUN_METADATA_WINDOW_LIMIT);
        return {
          ...previous,
          items: overflow === 0 ? combined : combined.slice(0, SESSION_RUN_METADATA_WINDOW_LIMIT),
          isLoadingOlder: false,
          error: null,
          olderOmitted: page.omitted.older,
          newerOmitted: previous.newerOmitted || overflow > 0,
          atLatest: previous.atLatest && overflow === 0,
          olderCursor: page.continuation.olderCursor,
        };
      });
    } catch (error) {
      if (generationRef.current !== generation || controller.signal.aborted || isAbort(error)) return;
      setState((previous) => previous.selectorKey === selectorKey ? { ...previous, isLoadingOlder: false, error: asError(error, "Could not load older Session run metadata.") } : previous);
    } finally {
      if (olderControllerRef.current === controller) olderControllerRef.current = null;
    }
  }, [selectorKey, stableSelector, visible.isLoading, visible.isLoadingOlder, visible.membershipHeadRunNumber, visible.olderCursor, visible.olderOmitted]);

  const returnToLatest = useCallback(() => setTailRevision((revision) => revision + 1), []);

  return {
    items: visible.items,
    isLoading: visible.isLoading,
    isLoadingOlder: visible.isLoadingOlder,
    error: visible.error,
    olderOmitted: visible.olderOmitted,
    newerOmitted: visible.newerOmitted,
    atLatest: visible.atLatest,
    consistency: "MembershipHeadOnly",
    loadOlder,
    returnToLatest,
  };
}
