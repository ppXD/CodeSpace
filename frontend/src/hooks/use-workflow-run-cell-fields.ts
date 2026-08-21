import { useCallback, useEffect, useRef, useState } from "react";

import {
  InvalidWorkflowRunCellFieldPageError,
  workflowRunCellFieldsApi,
  type WorkflowRunCellFieldDescriptor,
  type WorkflowRunCellFieldPage,
} from "@/api/workflowRunCellFieldsApi";
import { ApiError } from "@/api/request";
import type { WorkflowRunLazyFieldRead } from "@/api/workflowRunViewMetadataApi";

export const WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LOCAL_CAP = 512;

interface DescriptorState {
  key: string;
  identity: WorkflowRunCellFieldPage | null;
  fields: WorkflowRunCellFieldDescriptor[];
  pagesRead: number;
  loading: boolean;
  loadingMore: boolean;
  missing: boolean;
  earlierOmitted: boolean;
  error: Error | null;
  retryable: boolean;
}

function keyOf(read: WorkflowRunLazyFieldRead): string { return JSON.stringify(read); }
function empty(key: string, loading = false): DescriptorState {
  return { key, identity: null, fields: [], pagesRead: 0, loading, loadingMore: false, missing: false, earlierOmitted: false, error: null, retryable: false };
}
function asError(value: unknown): Error { return value instanceof Error ? value : new Error("Workflow Run cell fields failed."); }
function retryable(value: unknown): boolean { return value instanceof TypeError || value instanceof ApiError && value.status >= 500; }
function coordinate(read: WorkflowRunLazyFieldRead) {
  return { requestedRunId: read.requestedRunId, scope: read.scope, sourceRunId: read.sourceRunId, nodeId: read.nodeId, iterationKey: read.iterationKey };
}
function sameObservation(first: WorkflowRunCellFieldPage, next: WorkflowRunCellFieldPage): boolean {
  return first.stateRecordId.toLowerCase() === next.stateRecordId.toLowerCase()
    && first.stateRecordSequence === next.stateRecordSequence
    && first.firstStartedRecordId?.toLowerCase() === next.firstStartedRecordId?.toLowerCase()
    && first.firstStartedRecordSequence === next.firstStartedRecordSequence
    && first.status === next.status && first.inputsAvailability === next.inputsAvailability
    && first.outputsAvailability === next.outputsAvailability && first.errorAvailability === next.errorAvailability;
}

/** Descriptor metadata stays local and bounded; record/observation changes abort and discard the old page chain. */
export function useWorkflowRunCellFields(read: WorkflowRunLazyFieldRead, expanded: boolean) {
  const key = keyOf(read);
  const readRef = useRef(read);
  readRef.current = read;
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const [state, setState] = useState<DescriptorState>(() => empty(key));
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const currentGeneration = ++generation.current;
    controller.current?.abort();
    if (!expanded) {
      setState(empty(key));
      return;
    }
    const nextController = new AbortController();
    controller.current = nextController;
    setState(empty(key, true));
    void workflowRunCellFieldsApi.read(coordinate(readRef.current), null, nextController.signal).then((page) => {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      if (page === null) setState({ ...empty(key), missing: true });
      else setState({ ...empty(key), identity: page, fields: page.fields, pagesRead: 1 });
    }).catch((error: unknown) => {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...empty(key), error: asError(error), retryable: retryable(error) });
    });
    return () => nextController.abort();
  }, [expanded, key, revision]);

  const visible = state.key === key ? state : empty(key, expanded);
  const loadMore = useCallback(async () => {
    const cursor = visible.identity?.nextCursor;
    if (!expanded || cursor == null || visible.loading || visible.loadingMore) return;
    const currentGeneration = generation.current;
    const nextController = new AbortController();
    controller.current?.abort();
    controller.current = nextController;
    setState((current) => current.key === key ? { ...current, loadingMore: true, error: null, retryable: false } : current);
    try {
      const page = await workflowRunCellFieldsApi.read(coordinate(readRef.current), cursor, nextController.signal);
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => {
        if (current.key !== key) return current;
        if (page === null) return { ...empty(key), missing: true };
        if (current.identity === null || !sameObservation(current.identity, page))
          return { ...empty(key), error: new InvalidWorkflowRunCellFieldPageError() };
        const appended = [...current.fields, ...page.fields];
        const overflow = appended.length > WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LOCAL_CAP;
        return { ...current, identity: page, fields: overflow ? appended.slice(-WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LOCAL_CAP) : appended,
          pagesRead: current.pagesRead + 1, loadingMore: false, earlierOmitted: current.earlierOmitted || overflow, error: null };
      });
    } catch (error) {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => {
        if (current.key !== key) return current;
        const canRetry = retryable(error);
        return canRetry ? { ...current, loadingMore: false, error: asError(error), retryable: true }
          : { ...empty(key), error: asError(error) };
      });
    }
  }, [expanded, key, visible.identity?.nextCursor, visible.loading, visible.loadingMore]);

  const returnToFirst = useCallback(() => {
    ++generation.current;
    controller.current?.abort();
    setRevision((value) => value + 1);
  }, []);
  const retry = useCallback(() => {
    if (!visible.retryable) return;
    if (visible.fields.length === 0) returnToFirst();
    else void loadMore();
  }, [loadMore, returnToFirst, visible.fields.length, visible.retryable]);

  return { ...visible, hasMore: visible.identity?.nextCursor != null, loadMore, returnToFirst, retry };
}
