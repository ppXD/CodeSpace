import { useCallback, useEffect, useRef, useState } from "react";

import { agentsApi, type AgentRunEventDataRangeAvailable, type AgentRunEventDataRangeProblem, type AgentRunEventDataRangeResult } from "@/api/agents";

const EVENT_PAYLOAD_PAGE_BYTES = 64 * 1024;
const MAX_VISIBLE_EVENT_PAYLOAD_PAGES = 8;

interface PayloadChunk {
  offsetBytes: number;
  nextOffsetBytes: number;
  text: string;
}

interface PayloadState {
  key: string;
  chunks: PayloadChunk[];
  totalBytes: number | null;
  nextOffsetBytes: number | null;
  sha256: string | null;
  contentType: string | null;
  integrityVerified: boolean;
  loading: boolean;
  loadingMore: boolean;
  droppedEarlier: boolean;
  problem: AgentRunEventDataRangeProblem | null;
  retryOffsetBytes: number | null;
  error: Error | null;
}

const emptyPayload = (key: string): PayloadState => ({
  key, chunks: [], totalBytes: null, nextOffsetBytes: null, sha256: null, contentType: null, integrityVerified: false,
  loading: true, loadingMore: false, droppedEarlier: false, problem: null, retryOffsetBytes: null, error: null,
});

/**
 * Offloaded event bytes deliberately live only in this mounted disclosure. They never enter React Query, and closing
 * the disclosure destroys both the bytes and its AbortController. The eight-page window is at most 512 KiB.
 */
export function AgentRunEventPayload({ agentRunId, eventSequence, dataArtifactId }: { agentRunId: string; eventSequence: number; dataArtifactId: string }) {
  const key = `${agentRunId}:${eventSequence}:${dataArtifactId}`;
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const decoder = useRef(new TextDecoder("utf-8", { fatal: false }));
  const [state, setState] = useState<PayloadState>(() => emptyPayload(key));

  const readAt = useCallback(async (offsetBytes: number, replace: boolean) => {
    const currentGeneration = generation.current;
    controller.current?.abort();
    const request = new AbortController();
    controller.current = request;
    setState((current) => current.key === key
      ? { ...current, loading: replace, loadingMore: !replace, problem: null, retryOffsetBytes: null, error: null }
      : current);
    try {
      const result = await agentsApi.readRunEventDataRange(agentRunId, eventSequence, dataArtifactId, offsetBytes, EVENT_PAYLOAD_PAGE_BYTES, request.signal);
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => replace
        ? firstPayloadState(key, result, decoder.current, offsetBytes)
        : appendPayloadState(current, key, result, decoder.current, offsetBytes));
    } catch (error) {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => current.key === key
        ? { ...current, loading: false, loadingMore: false, error: error instanceof Error ? error : new Error("Agent Run event payload read failed.") }
        : current);
    }
  }, [agentRunId, dataArtifactId, eventSequence, key]);

  useEffect(() => {
    generation.current++;
    decoder.current = new TextDecoder("utf-8", { fatal: false });
    setState(emptyPayload(key));
    void readAt(0, true);
    return () => controller.current?.abort();
  }, [key, readAt]);

  const visible = state.key === key ? state : emptyPayload(key);
  const loadMore = useCallback(() => {
    if (visible.nextOffsetBytes == null || visible.loadingMore) return;
    void readAt(visible.nextOffsetBytes, false);
  }, [readAt, visible.loadingMore, visible.nextOffsetBytes]);
  const retry = useCallback(() => {
    if (visible.problem?.isRetryable !== true || visible.retryOffsetBytes == null) return;
    if (visible.retryOffsetBytes === 0) decoder.current = new TextDecoder("utf-8", { fatal: false });
    void readAt(visible.retryOffsetBytes, visible.retryOffsetBytes === 0);
  }, [readAt, visible.problem?.isRetryable, visible.retryOffsetBytes]);

  if (visible.loading) return <div className="tc-payload-notice">Loading offloaded payload…</div>;
  if (visible.error) return <div className="tc-payload-notice" data-state="error">Payload read failed · {visible.error.message}</div>;
  if (visible.problem) return (
    <div className="tc-payload-notice" data-state={visible.problem.availability}>
      {eventPayloadProblemLabel(visible.problem)} <code>{visible.problem.code}</code>
      {visible.problem.isRetryable && <button type="button" className="tc-payload-action" onClick={retry}>Retry payload range</button>}
    </div>
  );

  return (
    <div className="tc-payload">
      <div className="tc-payload-meta">
        {visible.totalBytes == null ? "Payload size unavailable" : `${visible.totalBytes.toLocaleString()} bytes`}
        {visible.contentType && <> · {visible.contentType}</>}
        {visible.sha256 && <> · sha256:{visible.sha256.slice(0, 12)}</>}
        <> · {visible.integrityVerified ? "whole-object integrity verified" : "bounded range; whole-object integrity not verified"}</>
      </div>
      {visible.droppedEarlier && <p className="tc-payload-window-note">Earlier payload bytes were removed from this view to keep the window bounded.</p>}
      {visible.chunks.map((chunk) => <pre key={chunk.offsetBytes} className="tc-args-full tc-payload-chunk">{chunk.text}</pre>)}
      {visible.nextOffsetBytes != null && <button type="button" className="tc-payload-action" disabled={visible.loadingMore} onClick={loadMore}>{visible.loadingMore ? "Loading…" : "Load next payload range"}</button>}
    </div>
  );
}

function firstPayloadState(key: string, result: AgentRunEventDataRangeResult, decoder: TextDecoder, requestedOffset: number): PayloadState {
  if (result.availability !== "Available") return { ...emptyPayload(key), loading: false, problem: result, retryOffsetBytes: requestedOffset };
  const chunk = decodePayloadChunk(result, decoder);
  return {
    key, chunks: result.bytes.length > 0 || chunk.text.length > 0 ? [chunk] : [], totalBytes: result.totalBytes,
    nextOffsetBytes: result.nextOffsetBytes, sha256: result.sha256, contentType: result.contentType, integrityVerified: result.integrityVerified,
    loading: false, loadingMore: false, droppedEarlier: false, problem: null, retryOffsetBytes: null, error: null,
  };
}

function appendPayloadState(current: PayloadState, key: string, result: AgentRunEventDataRangeResult, decoder: TextDecoder, requestedOffset: number): PayloadState {
  if (current.key !== key) return current;
  if (result.availability !== "Available") return { ...current, loadingMore: false, problem: result, retryOffsetBytes: requestedOffset };
  if (!samePayload(current, result)) {
    return { ...current, loadingMore: false, problem: { availability: "InvalidResponse", code: "event_data_metadata_changed", isRetryable: false }, retryOffsetBytes: null };
  }
  const chunk = decodePayloadChunk(result, decoder);
  const appended = result.bytes.length > 0 || chunk.text.length > 0 ? [...current.chunks, chunk] : current.chunks;
  const overflow = appended.length > MAX_VISIBLE_EVENT_PAYLOAD_PAGES;
  return {
    ...current, chunks: overflow ? appended.slice(-MAX_VISIBLE_EVENT_PAYLOAD_PAGES) : appended,
    nextOffsetBytes: result.nextOffsetBytes, integrityVerified: current.integrityVerified || result.integrityVerified,
    loadingMore: false, droppedEarlier: current.droppedEarlier || overflow, problem: null, retryOffsetBytes: null, error: null,
  };
}

function samePayload(current: PayloadState, result: AgentRunEventDataRangeAvailable) {
  return current.totalBytes === result.totalBytes && current.sha256?.toLowerCase() === result.sha256.toLowerCase()
    && current.contentType?.toLowerCase() === result.contentType.toLowerCase();
}

function decodePayloadChunk(result: AgentRunEventDataRangeAvailable, decoder: TextDecoder): PayloadChunk {
  return {
    offsetBytes: result.offsetBytes,
    nextOffsetBytes: result.nextOffsetBytes ?? result.totalBytes,
    text: decoder.decode(result.bytes, { stream: result.nextOffsetBytes != null }),
  };
}

function eventPayloadProblemLabel(problem: AgentRunEventDataRangeProblem): string {
  switch (problem.availability) {
    case "NotReferenced": return "Payload is not referenced.";
    case "InvalidRange": return "Invalid payload range.";
    case "MetadataMissing": return "Artifact metadata missing.";
    case "PhysicalObjectMissing": return "Stored payload bytes missing.";
    case "IntegrityFailure": return "Payload integrity check failed.";
    case "BackendUnavailable": return "Storage backend unavailable.";
    case "AccessDenied": return "Storage access denied.";
    case "Missing": return "Agent Run event payload is missing.";
    case "InvalidResponse": return "Invalid payload response.";
    default: return "Payload unavailable.";
  }
}
