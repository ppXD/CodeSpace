import { useCallback, useEffect, useRef, useState } from "react";

import {
  InvalidWorkflowRunCellFieldRangeResponseError,
  workflowRunCellFieldRangeApi,
  type WorkflowRunCellFieldRangePage,
  type WorkflowRunCellFieldReadIdentity,
} from "@/api/workflowRunCellFieldRangeApi";

export const WORKFLOW_RUN_CELL_FIELD_MAX_PAGES = 8;

interface ContentState {
  key: string;
  pages: WorkflowRunCellFieldRangePage[];
  pagesRead: number;
  loading: boolean;
  loadingMore: boolean;
  missing: boolean;
  earlierOmitted: boolean;
  failure: WorkflowRunCellFieldRangePage | null;
  error: Error | null;
}

function empty(key: string, loading: boolean): ContentState {
  return { key, pages: [], pagesRead: 0, loading, loadingMore: false, missing: false, earlierOmitted: false, failure: null, error: null };
}

function identityKey(identity: WorkflowRunCellFieldReadIdentity): string {
  return JSON.stringify(identity);
}

function errorOf(value: unknown): Error {
  return value instanceof Error ? value : new Error("Workflow Run cell-field content read failed.");
}

function compatible(previous: WorkflowRunCellFieldRangePage, next: WorkflowRunCellFieldRangePage): boolean {
  return previous.nextCursor === next.requestCursor && previous.offsetBytes + previous.returnedBytes === next.offsetBytes
    && previous.totalBytes === next.totalBytes && previous.source === next.source && previous.contentType === next.contentType
    && previous.integrityVerified === next.integrityVerified && !next.completeJsonValue;
}

/** Selected field bytes remain component-local; closing or switching identity destroys the bounded window. */
export function useWorkflowRunCellFieldContent(identity: WorkflowRunCellFieldReadIdentity, expanded: boolean) {
  const key = identityKey(identity);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const [revision, setRevision] = useState(0);
  const [state, setState] = useState<ContentState>(() => empty(key, false));

  useEffect(() => {
    const currentGeneration = ++generation.current;
    controller.current?.abort();
    if (!expanded) {
      setState(empty(key, false));
      return;
    }

    const nextController = new AbortController();
    controller.current = nextController;
    setState(empty(key, true));
    void workflowRunCellFieldRangeApi.read(identityRef.current, { cursor: null, offsetBytes: 0 }, nextController.signal).then((page) => {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      if (page === null) setState({ ...empty(key, false), missing: true });
      else if (page.availability !== "Available") setState({ ...empty(key, false), failure: page });
      else setState({ ...empty(key, false), pages: [page], pagesRead: 1 });
    }).catch((error: unknown) => {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...empty(key, false), error: errorOf(error) });
    });
    return () => nextController.abort();
  }, [expanded, key, revision]);

  const visible = state.key === key ? state : empty(key, expanded);
  const loadNext = useCallback(async () => {
    const previous = visible.pages.at(-1);
    if (!expanded || previous?.nextCursor == null || visible.loading || visible.loadingMore) return;
    const currentGeneration = generation.current;
    const nextController = new AbortController();
    controller.current?.abort();
    controller.current = nextController;
    setState((current) => current.key === key ? { ...current, loadingMore: true, failure: null, error: null } : current);
    try {
      const next = await workflowRunCellFieldRangeApi.read(identityRef.current, {
        cursor: previous.nextCursor,
        offsetBytes: previous.offsetBytes + previous.returnedBytes,
      }, nextController.signal);
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => {
        if (current.key !== key) return current;
        if (next === null) return { ...empty(key, false), missing: true };
        if (next.availability !== "Available") {
          const keepPages = next.availability === "BackendUnavailable" ? current.pages : [];
          return { ...current, pages: keepPages, loadingMore: false, failure: next, error: null };
        }
        const latest = current.pages.at(-1);
        if (latest === undefined || !compatible(latest, next)) {
          return { ...empty(key, false), error: new InvalidWorkflowRunCellFieldRangeResponseError() };
        }
        const appended = [...current.pages, next];
        const overflow = appended.length > WORKFLOW_RUN_CELL_FIELD_MAX_PAGES;
        return {
          ...current,
          pages: overflow ? appended.slice(-WORKFLOW_RUN_CELL_FIELD_MAX_PAGES) : appended,
          pagesRead: current.pagesRead + 1,
          loadingMore: false,
          earlierOmitted: current.earlierOmitted || overflow,
          failure: null,
          error: null,
        };
      });
    } catch (error) {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => current.key === key ? { ...current, loadingMore: false, error: errorOf(error) } : current);
    }
  }, [expanded, key, visible.loading, visible.loadingMore, visible.pages]);

  const returnToStart = useCallback(() => {
    ++generation.current;
    controller.current?.abort();
    setRevision((value) => value + 1);
  }, []);

  const retry = useCallback(() => {
    if (visible.failure?.availability !== "BackendUnavailable" || !visible.failure.retryable) return;
    if (visible.pages.length === 0) returnToStart();
    else void loadNext();
  }, [loadNext, returnToStart, visible.failure, visible.pages.length]);

  return {
    ...visible,
    hasNextPage: visible.pages.at(-1)?.nextCursor != null,
    canRetry: visible.failure?.availability === "BackendUnavailable" && visible.failure.retryable,
    loadNext,
    returnToStart,
    retry,
  };
}
