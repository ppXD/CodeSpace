import { useCallback, useEffect, useRef, useState } from "react";

import { workflowsApi, type RunRecordPageItem, type RunRecordPayloadRangeProblem } from "@/api/workflows";

import { JsonView } from "./JsonView";

export const RUN_RECORD_PAYLOAD_PAGE_BYTES = 64 * 1024;
export const RUN_RECORD_PAYLOAD_WINDOW_PAGES = 8;
export const RUN_RECORD_PAYLOAD_WINDOW_BYTES = RUN_RECORD_PAYLOAD_PAGE_BYTES * RUN_RECORD_PAYLOAD_WINDOW_PAGES;

interface PayloadViewState {
  loading: boolean;
  problem: RunRecordPayloadRangeProblem | null;
  totalBytes: number | null;
  windowStart: number;
  loadedEnd: number;
  pages: number;
  text: string;
  json: unknown;
  completeJson: boolean;
  nextOffset: number | null;
}

const emptyState = (windowStart = 0): PayloadViewState => ({
  loading: false, problem: null, totalBytes: null, windowStart, loadedEnd: windowStart, pages: 0,
  text: "", json: null, completeJson: false, nextOffset: null,
});

/**
 * Local-only body reader for one expanded metadata row. It deliberately bypasses React Query, admits at most eight
 * 64 KiB pages in one window, and keeps a fatal streaming UTF-8 decoder alive across page/window boundaries so a split
 * code point is never replaced with U+FFFD. Only a complete 0..EOF body no larger than the window is JSON-parsed.
 */
export function RunRecordPayload({ runId, record }: { runId: string; record: RunRecordPageItem }) {
  const [view, setView] = useState<PayloadViewState>(() => emptyState());
  const controllerRef = useRef<AbortController | null>(null);
  const generationRef = useRef(0);
  const decoderRef = useRef(new TextDecoder("utf-8", { fatal: true }));
  const totalRef = useRef<number | null>(null);
  const pagesRef = useRef(0);
  const bytesRef = useRef<Uint8Array<ArrayBufferLike>>(new Uint8Array());
  const textRef = useRef("");
  const retryRef = useRef<{ offset: number; newWindow: boolean } | null>(null);

  const read = useCallback(async (offset: number, newWindow: boolean) => {
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    const generation = ++generationRef.current;
    retryRef.current = { offset, newWindow };

    if (newWindow) {
      pagesRef.current = 0;
      bytesRef.current = new Uint8Array();
      textRef.current = "";
      setView({ ...emptyState(offset), loading: true, totalBytes: totalRef.current });
    } else {
      setView((previous) => ({ ...previous, loading: true, problem: null }));
    }

    let result;
    try {
      result = await workflowsApi.readRunRecordPayloadRange(runId, record.recordId, record.sequence, offset, RUN_RECORD_PAYLOAD_PAGE_BYTES, controller.signal);
    } catch (error) {
      if (controller.signal.aborted || (error instanceof Error && error.name === "AbortError")) return;
      result = { availability: "BackendUnavailable", code: "transport_unavailable", isRetryable: true } as const;
    }
    if (controller.signal.aborted || generationRef.current !== generation) return;
    if (controllerRef.current === controller) controllerRef.current = null;
    if (result.availability !== "Available") {
      setView((previous) => ({ ...previous, loading: false, problem: result }));
      return;
    }

    if (totalRef.current != null && totalRef.current !== result.totalBytes) {
      setView((previous) => ({ ...previous, loading: false, problem: invalid("record_payload_total_changed") }));
      return;
    }
    totalRef.current = result.totalBytes;
    pagesRef.current += 1;

    try {
      let json: unknown = null;
      let completeJson = false;
      if (result.totalBytes <= RUN_RECORD_PAYLOAD_WINDOW_BYTES) {
        bytesRef.current = append(bytesRef.current, result.bytes);
        if (result.nextOffsetBytes == null) {
          const raw = new TextDecoder("utf-8", { fatal: true }).decode(bytesRef.current);
          json = JSON.parse(raw) as unknown;
          completeJson = true;
          bytesRef.current = new Uint8Array();
        }
      } else {
        textRef.current += decoderRef.current.decode(result.bytes, { stream: result.nextOffsetBytes != null });
      }

      retryRef.current = null;
      setView((previous) => ({
        ...previous,
        loading: false,
        problem: null,
        totalBytes: result.totalBytes,
        loadedEnd: result.nextOffsetBytes ?? result.totalBytes,
        pages: pagesRef.current,
        text: textRef.current,
        json,
        completeJson,
        nextOffset: result.nextOffsetBytes,
      }));
    } catch {
      setView((previous) => ({ ...previous, loading: false, problem: invalid("invalid_record_payload_utf8_or_json") }));
    }
  }, [record.recordId, record.sequence, runId]);

  useEffect(() => {
    decoderRef.current = new TextDecoder("utf-8", { fatal: true });
    totalRef.current = null;
    pagesRef.current = 0;
    bytesRef.current = new Uint8Array();
    textRef.current = "";
    retryRef.current = null;
    void read(0, true);
    return () => {
      controllerRef.current?.abort();
      controllerRef.current = null;
      bytesRef.current = new Uint8Array();
      textRef.current = "";
    };
  }, [read]);

  const retry = () => {
    const target = retryRef.current;
    if (target) void read(target.offset, target.newWindow);
  };
  const loadMore = () => {
    if (view.nextOffset != null && view.pages < RUN_RECORD_PAYLOAD_WINDOW_PAGES) void read(view.nextOffset, false);
  };
  const nextWindow = () => {
    if (view.nextOffset != null && view.pages >= RUN_RECORD_PAYLOAD_WINDOW_PAGES) void read(view.nextOffset, true);
  };

  if (view.completeJson) return <JsonView data={view.json} />;

  return (
    <div className="run-record-payload-reader">
      {view.text && <pre className="run-record-payload-raw">{view.text}</pre>}
      {view.totalBytes != null && (
        <div className="run-record-payload-range">
          Requested canonical JSONB UTF-8 byte window {view.windowStart}–{view.loadedEnd} of {view.totalBytes}.
          {view.windowStart > 0 && " Earlier bytes are omitted; the first decoded code point may be a continuation begun before this window."}
          {view.nextOffset != null && " Later bytes are omitted until requested."}
        </div>
      )}
      {view.problem && <PayloadProblem problem={view.problem} onRetry={retry} />}
      {view.loading && <div>Loading payload…</div>}
      {!view.loading && !view.problem && view.nextOffset != null && view.pages < RUN_RECORD_PAYLOAD_WINDOW_PAGES && (
        <button type="button" onClick={loadMore}>Load more payload</button>
      )}
      {!view.loading && !view.problem && view.nextOffset != null && view.pages >= RUN_RECORD_PAYLOAD_WINDOW_PAGES && (
        <div className="run-record-payload-window-limit">
          This bounded preview window is limited to {RUN_RECORD_PAYLOAD_WINDOW_BYTES / 1024} KiB.
          {" "}<button type="button" onClick={nextWindow}>Next payload window</button>
        </div>
      )}
    </div>
  );
}

function PayloadProblem({ problem, onRetry }: { problem: RunRecordPayloadRangeProblem; onRetry: () => void }) {
  const message = problem.availability === "BackendUnavailable"
    ? "Payload is temporarily unavailable."
    : problem.availability === "InvalidResponse" ? "Payload response was invalid."
      : problem.availability === "Missing" ? "Payload is no longer available."
        : problem.availability === "AccessDenied" ? "Payload access was denied." : "Payload range is invalid.";
  return <div className="run-record-payload-problem">{message}{problem.isRetryable && <> {" "}<button type="button" onClick={onRetry}>Retry payload</button></>}</div>;
}

function invalid(code: string): RunRecordPayloadRangeProblem {
  return { availability: "InvalidResponse", code, isRetryable: false };
}

function append(existing: Uint8Array, next: Uint8Array): Uint8Array {
  if (existing.byteLength + next.byteLength > RUN_RECORD_PAYLOAD_WINDOW_BYTES) throw new Error("Record payload window overflow.");
  const combined = new Uint8Array(existing.byteLength + next.byteLength);
  combined.set(existing);
  combined.set(next, existing.byteLength);
  return combined;
}
