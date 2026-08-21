import { useEffect, useRef, useState } from "react";

import { workflowsApi, type WorkflowRunToolCallAttemptMetadata, type WorkflowRunToolCallDetail, type WorkflowRunToolCallMetadata } from "@/api/workflows";
import { useGovernedToolCallWindow } from "@/hooks/use-governed-tool-call-window";

interface DetailState {
  runId: string;
  callId: string;
  data: WorkflowRunToolCallDetail | null;
  isLoading: boolean;
  error: Error | null;
}

function message(error: unknown, fallback: string): Error {
  return error instanceof Error ? error : new Error(fallback);
}

function displayInstant(value: string | null): string {
  return value === null ? "Not recorded" : new Date(value).toLocaleString();
}

function MetadataField({ label, value, mono = false }: { label: string; value: string | number | null; mono?: boolean }) {
  return (
    <div className="gt-field">
      <span className="gt-field-label">{label}</span>
      <span className={mono ? "gt-field-value gt-mono" : "gt-field-value"}>{value ?? "Not recorded"}</span>
    </div>
  );
}

function Attempt({ attempt }: { attempt: WorkflowRunToolCallAttemptMetadata }) {
  return (
    <li className="gt-attempt">
      <div className="gt-attempt-head">
        <strong>Attempt {attempt.attemptOrdinal}</strong>
        <span className="gt-state" data-state={attempt.status}>{attempt.status}</span>
      </div>
      <div className="gt-fields">
        <MetadataField label="Capture" value={`${attempt.captureCompleteness} · ${attempt.captureSource}`} />
        <MetadataField label="Admission lower-bound" value={displayInstant(attempt.startedAt)} />
        <MetadataField label="Completed" value={displayInstant(attempt.completedAt)} />
        <MetadataField label="Last observed" value={displayInstant(attempt.lastModifiedAt)} />
        {attempt.errorCode && <MetadataField label="Error code" value={attempt.errorCode} mono />}
      </div>
    </li>
  );
}

function CallDetail({ detail }: { detail: WorkflowRunToolCallDetail }) {
  const call = detail.call;
  return (
    <section className="gt-detail" aria-label={`Governed tool detail ${call.toolName}`}>
      <div className="gt-detail-head">
        <div>
          <span className="gt-eyebrow">Selected observation</span>
          <h3>{call.toolName}</h3>
        </div>
        <span className="gt-state" data-state={call.state}>{call.state}</span>
      </div>
      <div className="gt-fields">
        <MetadataField label="Stable call id" value={call.toolCallId} mono />
        <MetadataField label="Adapter" value={call.toolAdapterKind} mono />
        <MetadataField label="Effect" value={call.effectClass} />
        <MetadataField label="Agent admission" value={`#${call.callOrdinal}`} />
        <MetadataField label="Source" value={call.sourceKind} mono />
        <MetadataField label="Source correlation" value={call.sourceCorrelationId} mono />
        <MetadataField label="Capture" value={`${call.captureCompleteness} · ${call.captureSource}`} />
        <MetadataField label="Created" value={displayInstant(call.createdAt)} />
        <MetadataField label="Terminal" value={displayInstant(call.terminalAt)} />
        {call.errorCode && <MetadataField label="Error code" value={call.errorCode} mono />}
      </div>

      <div className="gt-attempt-note">Started time is a source admission lower-bound, not provider wire start.</div>
      <h4 className="gt-attempt-title">Attempts · {detail.attempts.length}</h4>
      {detail.attempts.length === 0 ? <div className="gt-empty">No attempt metadata was recorded.</div> : (
        <ol className="gt-attempts">{detail.attempts.map((attempt) => <Attempt key={attempt.attemptOrdinal} attempt={attempt} />)}</ol>
      )}
      {detail.attemptsTruncated && <div className="gt-omitted">100 attempts shown at most; additional attempts were omitted.</div>}
    </section>
  );
}

function CallRow({ call, selected, onSelect }: { call: WorkflowRunToolCallMetadata; selected: boolean; onSelect: () => void }) {
  return (
    <li>
      <button type="button" className="gt-call" data-selected={selected || undefined} aria-pressed={selected} onClick={onSelect}>
        <span className="gt-call-main">
          <strong>{call.toolName}</strong>
          <span className="gt-adapter">{call.toolAdapterKind}</span>
        </span>
        <span className="gt-call-meta">
          <span className="gt-state" data-state={call.state}>{call.state}</span>
          <span>{call.effectClass}</span>
          <span>Agent admission #{call.callOrdinal}</span>
          <time dateTime={call.terminalAt ?? call.lastModifiedAt}>{displayInstant(call.terminalAt ?? call.lastModifiedAt)}</time>
        </span>
      </button>
    </li>
  );
}

/** Independent Workflow Run observation surface. It never unions native Agent events or Session Room model calls. */
export function GovernedToolsPanel({ runId, active }: { runId: string; active: boolean }) {
  const window = useGovernedToolCallWindow(runId, active);
  const [selection, setSelection] = useState<{ runId: string; callId: string } | null>(null);
  const [detailRevision, setDetailRevision] = useState(0);
  const [detail, setDetail] = useState<DetailState | null>(null);
  const detailGenerationRef = useRef(0);
  const selectedCallId = selection?.runId === runId ? selection.callId : null;

  useEffect(() => {
    const generation = ++detailGenerationRef.current;
    if (selectedCallId === null) return;
    const controller = new AbortController();
    void workflowsApi.getRunToolCall(runId, selectedCallId, controller.signal).then((data) => {
      if (controller.signal.aborted || detailGenerationRef.current !== generation) return;
      if (data === null) throw new Error("This governed tool observation is no longer available.");
      setDetail({ runId, callId: selectedCallId, data, isLoading: false, error: null });
    }).catch((error: unknown) => {
      if (controller.signal.aborted || detailGenerationRef.current !== generation || (error instanceof Error && error.name === "AbortError")) return;
      setDetail({ runId, callId: selectedCallId, data: null, isLoading: false, error: message(error, "Could not load governed tool detail.") });
    });
    return () => controller.abort();
  }, [detailRevision, runId, selectedCallId]);

  const visibleDetail = detail?.runId === runId && detail.callId === selectedCallId ? detail : null;
  const selectCall = (callId: string) => {
    if (selectedCallId === callId) return;
    setSelection({ runId, callId });
    setDetail({ runId, callId, data: null, isLoading: true, error: null });
  };
  const retryDetail = () => {
    if (selectedCallId === null) return;
    setDetail({ runId, callId: selectedCallId, data: null, isLoading: true, error: null });
    setDetailRevision((revision) => revision + 1);
  };

  return (
    <section className="gt-panel" aria-label="Governed tools">
      <div className="gt-scope">
        <div>
          <span className="gt-scope-tag">Terminal governed side effects only</span>
          <p>These are durable, metadata-only observations from the governed side-effect ledger. CLI and native tool activity stays in each Agent terminal.</p>
        </div>
        {active && <span className="gt-live">Refreshing about once a minute</span>}
      </div>

      {window.error && (
        <div className="gt-error" role="status">
          <span>{window.error.message}</span>
          <button type="button" onClick={window.returnToLatest}>Retry latest</button>
        </div>
      )}

      {window.isLoading && window.calls.length === 0 ? <div className="gt-empty">Loading governed tool observations…</div> : window.calls.length === 0 ? (
        <div className="gt-empty">No terminal governed side-effect observations have been projected for this run.</div>
      ) : (
        <div className="gt-layout">
          <div className="gt-list-wrap">
            <ol className="gt-list">
              {window.calls.map((call) => <CallRow key={call.toolCallId} call={call} selected={selectedCallId === call.toolCallId} onSelect={() => selectCall(call.toolCallId)} />)}
            </ol>
            <div className="gt-paging">
              {window.olderCallsOmitted && <span>Older observations are omitted until loaded.</span>}
              {window.newerCallsOmitted && <span>Newer observations were omitted by the 512-row local window.</span>}
              {window.hasOlder && <button type="button" disabled={window.isLoadingOlder} onClick={() => void window.loadOlder()}>{window.isLoadingOlder ? "Loading…" : "Load older"}</button>}
              {!window.atLatest && <button type="button" onClick={window.returnToLatest}>Return to latest</button>}
            </div>
          </div>

          {selectedCallId === null ? <div className="gt-detail gt-empty">Select an observation to read its bounded metadata.</div> : visibleDetail?.isLoading ? (
            <div className="gt-detail gt-empty">Loading selected observation…</div>
          ) : visibleDetail?.error ? (
            <div className="gt-detail gt-empty">
              <span>{visibleDetail.error.message}</span>
              <button type="button" onClick={retryDetail}>Retry detail</button>
            </div>
          ) : visibleDetail?.data ? <CallDetail detail={visibleDetail.data} /> : null}
        </div>
      )}
    </section>
  );
}
