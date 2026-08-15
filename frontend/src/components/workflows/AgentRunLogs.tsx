import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { agentsApi, type AgentRunLogPage, type AgentRunLogRangeProblem, type AgentRunLogRangeResult, type AgentRunLogStatus, type AgentRunLogStreamSummary } from "@/api/agents";
import { ApiError } from "@/api/request";

const LOG_PAGE_BYTES = 64 * 1024;
const LOG_STREAM_PAGE_SIZE = 25;
const MAX_VISIBLE_CHUNKS = 8;

interface StreamListState {
  runId: string;
  items: AgentRunLogStreamSummary[];
  nextCursor: string | null;
  loading: boolean;
  loadingMore: boolean;
  missing: boolean;
  error: Error | null;
  refreshingStreamId: string | null;
  refreshIssue: { streamId: string; kind: "Missing" | "Error"; error: Error | null } | null;
  contentEpoch: number;
  restartEpoch: number;
}

interface VisibleChunk {
  offsetBytes: number;
  nextOffsetBytes: number;
  text: string;
}

interface ContentState {
  key: string;
  chunks: VisibleChunk[];
  totalBytes: number;
  headOffsetBytes: number;
  nextOffsetBytes: number | null;
  loading: boolean;
  loadingMore: boolean;
  droppedEarlier: boolean;
  problem: AgentRunLogRangeProblem | null;
  error: Error | null;
}

const emptyList = (runId: string): StreamListState => ({ runId, items: [], nextCursor: null, loading: true, loadingMore: false, missing: false, error: null, refreshingStreamId: null, refreshIssue: null, contentEpoch: 0, restartEpoch: -1 });
const emptyContent = (key: string): ContentState => ({ key, chunks: [], totalBytes: 0, headOffsetBytes: 0, nextOffsetBytes: null, loading: true, loadingMore: false, droppedEarlier: false, problem: null, error: null });

function mergeStreams(existing: AgentRunLogStreamSummary[], page: AgentRunLogPage): AgentRunLogStreamSummary[] {
  const byId = new Map(existing.map((item) => [item.streamId, item]));
  for (const item of page.items) {
    if (byId.has(item.streamId)) throw new Error("Agent Run log metadata pagination repeated a stream identity.");
    byId.set(item.streamId, item);
  }
  return [...byId.values()];
}

function useStreamMetadata(agentRunId: string) {
  const generation = useRef(0);
  const listController = useRef<AbortController | null>(null);
  const pageController = useRef<AbortController | null>(null);
  const refreshController = useRef<AbortController | null>(null);
  const [state, setState] = useState<StreamListState>(() => emptyList(agentRunId));

  useEffect(() => {
    const currentGeneration = ++generation.current;
    listController.current?.abort();
    pageController.current?.abort();
    refreshController.current?.abort();
    const request = new AbortController();
    listController.current = request;
    setState(emptyList(agentRunId));
    void agentsApi.listRunLogs(agentRunId, null, LOG_STREAM_PAGE_SIZE, request.signal).then((page) => {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...emptyList(agentRunId), items: page?.items ?? [], nextCursor: page?.nextCursor ?? null, loading: false, missing: page == null });
    }).catch((error: unknown) => {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...emptyList(agentRunId), loading: false, error: error instanceof Error ? error : new Error("Agent Run log metadata read failed.") });
    });
    return () => {
      request.abort();
      pageController.current?.abort();
      refreshController.current?.abort();
    };
  }, [agentRunId]);

  const visible = state.runId === agentRunId ? state : emptyList(agentRunId);
  const loadMore = useCallback(async () => {
    if (!visible.nextCursor || visible.loadingMore) return;
    const currentGeneration = generation.current;
    pageController.current?.abort();
    const request = new AbortController();
    pageController.current = request;
    setState((current) => current.runId === agentRunId ? { ...current, loadingMore: true, error: null } : current);
    try {
      const page = await agentsApi.listRunLogs(agentRunId, visible.nextCursor, LOG_STREAM_PAGE_SIZE, request.signal);
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      if (page != null) mergeStreams(visible.items, page);
      setState((current) => current.runId === agentRunId
        ? { ...current, items: page == null ? current.items : [...current.items, ...page.items], nextCursor: page?.nextCursor ?? null, loadingMore: false, missing: page == null, error: null }
        : current);
    } catch (error) {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => current.runId === agentRunId ? { ...current, loadingMore: false, error: error instanceof Error ? error : new Error("Agent Run log metadata read failed.") } : current);
    }
  }, [agentRunId, visible.items, visible.loadingMore, visible.nextCursor]);
  const refreshStream = useCallback(async (streamId: string, restart = false) => {
    const currentGeneration = generation.current;
    refreshController.current?.abort();
    const request = new AbortController();
    refreshController.current = request;
    setState((current) => current.runId === agentRunId ? { ...current, refreshingStreamId: streamId, refreshIssue: null } : current);
    try {
      const stream = await agentsApi.getRunLog(agentRunId, streamId, request.signal);
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => {
        if (current.runId !== agentRunId) return current;
        if (stream == null) return { ...current, refreshingStreamId: null, refreshIssue: { streamId, kind: "Missing", error: null }, contentEpoch: current.contentEpoch + 1 };
        const contentEpoch = current.contentEpoch + 1;
        return { ...current, items: current.items.map((item) => item.streamId === streamId ? stream : item), refreshingStreamId: null, refreshIssue: null, contentEpoch, restartEpoch: restart ? contentEpoch : current.restartEpoch };
      });
    } catch (error) {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => current.runId === agentRunId ? { ...current, refreshingStreamId: null, refreshIssue: { streamId, kind: "Error", error: error instanceof Error ? error : new Error("Agent Run log metadata refresh failed.") } } : current);
    }
  }, [agentRunId]);
  return { ...visible, loadMore, refreshStream };
}

function useBoundedLogContent(agentRunId: string, stream: AgentRunLogStreamSummary, metadataEpoch: number, forceRestart: boolean) {
  const key = `${agentRunId}:${stream.streamId}`;
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const decoder = useRef(new TextDecoder());
  const priorMetadata = useRef<AgentRunLogStreamSummary | null>(null);
  const [state, setState] = useState<ContentState>(() => emptyContent(key));
  const stateRef = useRef(state);
  stateRef.current = state;

  useEffect(() => {
    const currentGeneration = ++generation.current;
    controller.current?.abort();
    const request = new AbortController();
    controller.current = request;
    const current = stateRef.current.key === key ? stateRef.current : emptyContent(key);
    const appendAtHead = !forceRestart && canContinueAtHead(priorMetadata.current, stream, current);
    const requestedOffset = appendAtHead ? current.headOffsetBytes : 0;
    priorMetadata.current = stream;
    if (appendAtHead) setState((value) => value.key === key ? { ...value, loading: false, loadingMore: true, problem: null, error: null } : value);
    else {
      decoder.current = new TextDecoder("utf-8", { fatal: false });
      setState(emptyContent(key));
    }
    void agentsApi.readRunLogRange(agentRunId, stream.streamId, requestedOffset, LOG_PAGE_BYTES, request.signal).then((result) => {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      const admitted = admitRange(result, stream);
      setState((value) => appendAtHead
        ? appendContentState(value, key, admitted, decoder.current, stream.status)
        : firstContentState(key, admitted, decoder.current, stream.status));
    }).catch((error: unknown) => {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((value) => appendAtHead && value.key === key
        ? { ...value, loadingMore: false, error: error instanceof Error ? error : new Error("Agent Run log content read failed.") }
        : { ...emptyContent(key), loading: false, error: error instanceof Error ? error : new Error("Agent Run log content read failed.") });
    });
    return () => request.abort();
  }, [agentRunId, forceRestart, key, metadataEpoch, stream]);

  const visible = state.key === key ? state : emptyContent(key);
  const loadMore = useCallback(async () => {
    if (visible.nextOffsetBytes == null || visible.loadingMore) return;
    const currentGeneration = generation.current;
    const requestedOffset = visible.nextOffsetBytes;
    controller.current?.abort();
    const request = new AbortController();
    controller.current = request;
    setState((current) => current.key === key ? { ...current, loadingMore: true, problem: null, error: null } : current);
    try {
      const result = await agentsApi.readRunLogRange(agentRunId, stream.streamId, requestedOffset, LOG_PAGE_BYTES, request.signal);
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => appendContentState(current, key, admitRange(result, stream), decoder.current, stream.status));
    } catch (error) {
      if (request.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => current.key === key ? { ...current, loadingMore: false, error: error instanceof Error ? error : new Error("Agent Run log content read failed.") } : current);
    }
  }, [agentRunId, key, stream, visible.loadingMore, visible.nextOffsetBytes]);

  return { ...visible, loadMore };
}

function firstContentState(key: string, result: AgentRunLogRangeResult, decoder: TextDecoder, status: AgentRunLogStatus): ContentState {
  if (result.availability !== "Available") return { ...emptyContent(key), loading: false, problem: result };
  const chunk = decodeChunk(result, decoder, status);
  return { key, chunks: result.bytes.length > 0 || chunk.text.length > 0 ? [chunk] : [], totalBytes: result.totalBytes, headOffsetBytes: result.nextOffsetBytes, nextOffsetBytes: result.hasMore ? result.nextOffsetBytes : null, loading: false, loadingMore: false, droppedEarlier: false, problem: null, error: null };
}

function appendContentState(current: ContentState, key: string, result: AgentRunLogRangeResult, decoder: TextDecoder, status: AgentRunLogStatus): ContentState {
  if (current.key !== key) return current;
  if (result.availability !== "Available") return { ...current, loadingMore: false, problem: result };
  const chunk = decodeChunk(result, decoder, status);
  const appended = result.bytes.length === 0 && chunk.text.length === 0 ? current.chunks : [...current.chunks, chunk];
  const overflow = appended.length > MAX_VISIBLE_CHUNKS;
  return {
    ...current,
    chunks: overflow ? appended.slice(-MAX_VISIBLE_CHUNKS) : appended,
    totalBytes: result.totalBytes,
    headOffsetBytes: result.nextOffsetBytes,
    nextOffsetBytes: result.hasMore ? result.nextOffsetBytes : null,
    loadingMore: false,
    droppedEarlier: current.droppedEarlier || overflow,
    problem: null,
    error: null,
  };
}

function canContinueAtHead(previous: AgentRunLogStreamSummary | null, next: AgentRunLogStreamSummary, content: ContentState) {
  return previous?.status === "Open" && next.revision >= previous.revision && next.totalBytes >= content.headOffsetBytes
    && normalizedMediaType(previous.contentType) === normalizedMediaType(next.contentType)
    && normalizedEncoding(previous.contentEncoding) === normalizedEncoding(next.contentEncoding);
}

function decodeChunk(result: Extract<AgentRunLogRangeResult, { availability: "Available" }>, decoder: TextDecoder, status: AgentRunLogStatus): VisibleChunk {
  return { offsetBytes: result.offsetBytes, nextOffsetBytes: result.nextOffsetBytes, text: decoder.decode(result.bytes, { stream: result.hasMore || status === "Open" }) };
}

function admitRange(result: AgentRunLogRangeResult, stream: AgentRunLogStreamSummary): AgentRunLogRangeResult {
  if (result.availability !== "Available") return result;
  const identityMatches = result.revision === stream.revision && result.totalBytes === stream.totalBytes
    && normalizedMediaType(result.contentType) === normalizedMediaType(stream.contentType)
    && normalizedEncoding(result.contentEncoding) === normalizedEncoding(stream.contentEncoding);
  return identityMatches ? result : { availability: "InvalidResponse", code: "range_metadata_mismatch", isRetryable: true };
}

export function AgentRunLogs({ agentRunId }: { agentRunId: string }) {
  const metadata = useStreamMetadata(agentRunId);
  const [selectedStreamId, setSelectedStreamId] = useState<string | null>(null);
  const selected = metadata.items.find((item) => item.streamId === selectedStreamId) ?? metadata.items[0] ?? null;
  const selectedRefreshIssue = selected && metadata.refreshIssue?.streamId === selected.streamId ? metadata.refreshIssue : null;

  if (metadata.loading) return <div className="agent-terminal-empty">Loading durable log metadata…</div>;
  if (metadata.missing) return <LogNotice title="Missing" detail="No durable log record exists for this Agent Run." />;
  if (metadata.error && metadata.items.length === 0) return <LogNotice title="Metadata unavailable" detail={failureDetail(metadata.error)} />;
  if (metadata.items.length === 0) return <LogNotice title="No streams" detail="No log streams have been captured for this Agent Run." />;

  return (
    <section className="agent-run-logs" aria-label="Durable Agent Run logs">
      <div className="agent-run-log-streams" role="tablist" aria-label="Log streams">
        {metadata.items.map((item) => (
          <button key={item.streamId} type="button" role="tab" aria-selected={item.streamId === selected?.streamId}
            aria-label={`${streamLabel(item.streamKind)} · ${statusLabel(item.status)}`} data-active={item.streamId === selected?.streamId || undefined}
            onClick={() => setSelectedStreamId(item.streamId)}>
            <strong>{streamLabel(item.streamKind)}</strong><span data-state={item.status}>{statusLabel(item.status)}</span><small>{formatBytes(item.totalBytes)}</small>
          </button>
        ))}
        {metadata.nextCursor && <button type="button" className="agent-run-log-more-streams" disabled={metadata.loadingMore} onClick={() => void metadata.loadMore()}>{metadata.loadingMore ? "Loading…" : "Load more streams"}</button>}
      </div>
      {metadata.error && <LogNotice title="More metadata unavailable" detail={failureDetail(metadata.error)} />}
      {selectedRefreshIssue?.kind === "Missing" && <LogNotice title="Stream metadata missing" detail="The exact stream metadata no longer exists; stale bytes were not re-read." />}
      {selectedRefreshIssue?.kind === "Error" && <LogNotice title="Metadata refresh failed" detail={`${failureDetail(selectedRefreshIssue.error!)} Existing bytes still describe the prior revision.`} />}
      {selected && selectedRefreshIssue?.kind !== "Missing" && representationProblem(selected) == null
        ? <AgentRunLogContent key={`${agentRunId}:${selected.streamId}`} agentRunId={agentRunId} stream={selected} metadataEpoch={metadata.contentEpoch} forceRestart={metadata.restartEpoch === metadata.contentEpoch}
            refreshing={metadata.refreshingStreamId === selected.streamId} onRefresh={() => metadata.refreshStream(selected.streamId)} onRestart={() => metadata.refreshStream(selected.streamId, true)} />
        : selected && selectedRefreshIssue?.kind !== "Missing" && <UnsupportedAgentRunLogContent stream={selected} refreshing={metadata.refreshingStreamId === selected.streamId} onRefresh={() => metadata.refreshStream(selected.streamId)} />}
    </section>
  );
}

function AgentRunLogContent({ agentRunId, stream, metadataEpoch, forceRestart, refreshing, onRefresh, onRestart }: { agentRunId: string; stream: AgentRunLogStreamSummary; metadataEpoch: number; forceRestart: boolean; refreshing: boolean; onRefresh: () => Promise<void>; onRestart: () => Promise<void> }) {
  const content = useBoundedLogContent(agentRunId, stream, metadataEpoch, forceRestart);
  const start = content.chunks[0]?.offsetBytes ?? 0;
  const end = content.chunks.at(-1)?.nextOffsetBytes ?? 0;
  return (
    <div className="agent-run-log-content">
      <StreamState stream={stream} />
      <LogFacts stream={stream} disabled={refreshing || content.loading || content.loadingMore} onRefresh={onRefresh} />
      {content.loading ? <div className="agent-terminal-empty">Reading the first {formatBytes(LOG_PAGE_BYTES)}…</div>
        : content.chunks.length === 0 && content.problem ? <ReadProblem problem={content.problem} />
        : content.chunks.length === 0 && content.error ? <LogNotice title="Read failed" detail={failureDetail(content.error)} />
        : <>
            <div className="agent-run-log-window">
              <span>Showing bytes {start.toLocaleString()}–{end.toLocaleString()} of {content.totalBytes.toLocaleString()}</span>
              {content.droppedEarlier && <button type="button" disabled={refreshing} onClick={() => void onRestart()}>Start over</button>}
            </div>
            {content.droppedEarlier && <p className="agent-run-log-window-note">Earlier bytes were removed from this view to keep the DOM and memory bounded.</p>}
            {content.chunks.every((chunk) => chunk.text.length === 0)
              ? <p className="agent-run-log-empty-bytes">{stream.status === "Open" ? "No bytes captured at this offset yet; this stream remains open." : "This completed range contains zero bytes."}</p>
              : <pre className="agent-run-log-pre">{content.chunks.map((chunk) => <span className="agent-run-log-chunk" key={chunk.offsetBytes}>{chunk.text}</span>)}</pre>}
            {content.problem && <ReadProblem problem={content.problem} />}
            {content.error && <LogNotice title="Next range failed" detail={failureDetail(content.error)} />}
            {content.nextOffsetBytes != null && <button type="button" className="agent-run-log-load" disabled={content.loadingMore} onClick={() => void content.loadMore()}>{content.loadingMore ? "Loading…" : "Load next range"}</button>}
          </>}
    </div>
  );
}

function UnsupportedAgentRunLogContent({ stream, refreshing, onRefresh }: { stream: AgentRunLogStreamSummary; refreshing: boolean; onRefresh: () => Promise<void> }) {
  return (
    <div className="agent-run-log-content">
      <StreamState stream={stream} />
      <LogFacts stream={stream} disabled={refreshing} onRefresh={onRefresh} />
      <LogNotice title="Unsupported log representation" detail={representationProblem(stream)!} />
    </div>
  );
}

function LogFacts({ stream, disabled, onRefresh }: { stream: AgentRunLogStreamSummary; disabled: boolean; onRefresh: () => Promise<void> }) {
  return <div className="agent-run-log-facts"><span>{stream.contentType}</span><span>{stream.contentEncoding ?? "unencoded"}</span><span>{stream.captureSource}</span><span>revision {stream.revision}</span><span>{stream.segmentCount} segments</span><button type="button" disabled={disabled} onClick={() => void onRefresh()}>{stream.status === "Open" ? "Refresh stream" : "Retry read"}</button></div>;
}

function StreamState({ stream }: { stream: AgentRunLogStreamSummary }) {
  const detail = useMemo(() => {
    switch (stream.status) {
      case "Open": return "Capture is open; more bytes may arrive.";
      case "Completed": return "Capture completed and its durable metadata was committed.";
      case "Truncated": return "Capture is explicitly truncated; displayed bytes are not the whole source.";
      case "Unavailable": return "Capture metadata marks this stream unavailable.";
      case "Corrupt": return "Capture metadata marks this stream corrupt; do not trust its bytes.";
      case "CaptureFailed": return `Capture failed${stream.errorCode ? ` (${stream.errorCode})` : ""}; any stored prefix is partial.`;
      default: return "Unknown stream state; content is not assumed complete.";
    }
  }, [stream.errorCode, stream.status]);
  return <p className="agent-run-log-state" data-state={stream.status}><strong>{statusLabel(stream.status)}</strong> — {detail}</p>;
}

function ReadProblem({ problem }: { problem: AgentRunLogRangeProblem }) {
  const title = (() => {
    switch (problem.availability) {
      case "Missing": case "PhysicalObjectMissing": return "Stored log bytes are missing";
      case "IntegrityFailure": return "Stored log bytes are corrupt";
      case "BackendUnavailable": return "Storage backend unavailable";
      case "AccessDenied": return "Storage access denied";
      case "ProviderTimeout": return "Storage provider timed out";
      case "InvalidRange": return "Invalid log range";
      case "Unsupported": return "Storage reader unsupported";
      case "InvalidResponse": return "Invalid log response";
    }
  })();
  return <LogNotice title={title} detail={`${problem.code}${problem.isRetryable ? " · retryable" : " · not retryable"}`} />;
}

function LogNotice({ title, detail }: { title: string; detail: string }) {
  return <div className="agent-run-log-notice" role="status"><strong>{title}</strong><span>{detail}</span></div>;
}

function failureDetail(error: Error) {
  return error instanceof ApiError ? `HTTP ${error.status} · ${error.code}` : "The request ended before a typed storage outcome was returned.";
}

function representationProblem(stream: AgentRunLogStreamSummary): string | null {
  const mediaType = normalizedMediaType(stream.contentType);
  const encoding = normalizedEncoding(stream.contentEncoding);
  if (!mediaType.startsWith("text/")) return `Content type ${stream.contentType} is binary or unknown; no UTF-8 decoding was attempted.`;
  if (encoding != null && encoding !== "utf-8") return `Content encoding ${stream.contentEncoding} is not supported; encoded bytes were not requested.`;
  return null;
}

function normalizedMediaType(value: string) {
  return value.trim().toLowerCase();
}

function normalizedEncoding(value: string | null) {
  return value?.trim().toLowerCase() ?? null;
}

function streamLabel(kind: string) {
  switch (kind) {
    case "stdout/v1": return "stdout";
    case "stderr/v1": return "stderr";
    case "transcript/v1": return "transcript";
    case "debug/v1": return "debug";
    default: return kind;
  }
}

function statusLabel(status: AgentRunLogStatus) {
  return status.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(value < 10 * 1024 ? 1 : 0)} KiB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MiB`;
}
