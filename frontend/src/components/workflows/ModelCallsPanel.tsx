import { useEffect, useState } from "react";

import { workflowsApi, type WorkflowRunModelCallDetailMetadata, type WorkflowRunModelCallListItem } from "@/api/workflows";
import { WorkflowRunStableModelCallContent, type WorkflowRunModelCallTab } from "@/components/sessions/WorkflowRunModelCallContent";

export function ModelCallsPanel({ runId, active }: { runId: string; active: boolean }) {
  const [calls, setCalls] = useState<WorkflowRunModelCallListItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<WorkflowRunModelCallDetailMetadata | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [detailLoading, setDetailLoading] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [callTab, setCallTab] = useState<WorkflowRunModelCallTab>("result");

  useEffect(() => {
    const controller = new AbortController();
    const load = () => void workflowsApi.pageRunModelCalls(runId, undefined, 100, controller.signal).then((page) => {
      if (!page) {
        setCalls([]);
        setCursor(null);
        setError("This run is no longer available.");
        setLoading(false);
        return;
      }
      setCalls(page.items);
      setCursor(page.nextCursor);
      setError(null);
      setLoading(false);
    }).catch((reason: unknown) => {
      if (!controller.signal.aborted) { setError(reason instanceof Error ? reason.message : "Could not load model calls."); setLoading(false); }
    });
    load();
    const timer = active ? window.setInterval(load, 60_000) : null;
    return () => { controller.abort(); if (timer !== null) window.clearInterval(timer); };
  }, [active, runId]);

  useEffect(() => {
    if (!selected) return;
    const controller = new AbortController();
    void workflowsApi.getRunModelCallById(runId, selected, controller.signal).then((result) => {
      if (controller.signal.aborted) return;
      setDetail(result);
      setDetailLoading(false);
      if (!result) setError("The selected model call is no longer available.");
    }).catch((reason: unknown) => {
      if (!controller.signal.aborted) { setError(reason instanceof Error ? reason.message : "Could not load model-call detail."); setDetailLoading(false); }
    });
    return () => controller.abort();
  }, [runId, selected]);

  useEffect(() => {
    if (!selected || !detail || ![...detail.bodies, ...detail.attempts.flatMap((attempt) => attempt.bodies)]
      .some((body) => body.captureHealth === "Pending" || body.captureHealth === "Materializing" || body.captureHealth === "Retry")) return;
    const controller = new AbortController();
    const timer = window.setInterval(() => void workflowsApi.getRunModelCallById(runId, selected, controller.signal).then((result) => {
      if (!controller.signal.aborted && result) setDetail(result);
    }).catch((reason: unknown) => {
      if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "Could not refresh model-call capture state.");
    }), 3_000);
    return () => { controller.abort(); window.clearInterval(timer); };
  }, [detail, runId, selected]);

  const loadOlder = async () => {
    if (!cursor || loadingOlder) return;
    setLoadingOlder(true);
    try {
      const page = await workflowsApi.pageRunModelCalls(runId, cursor, 100);
      if (!page) { setError("This run is no longer available."); return; }
      setCalls((current) => [...current, ...page.items]);
      setCursor(page.nextCursor);
      setError(null);
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : "Could not load older model calls.");
    } finally {
      setLoadingOlder(false);
    }
  };

  return <section className="gt-panel" aria-label="Model calls">
    <div className="gt-scope"><div><span className="gt-scope-tag">All model-call producers</span><p>In-process, CLI-native, and harness calls share this stable, body-free index. Bodies load only after selection.</p></div></div>
    {error && <div className="gt-error" role="status">{error}</div>}
    {loading ? <div className="gt-empty">Loading model calls…</div> : calls.length === 0 ? <div className="gt-empty">No model calls have been projected for this run.</div> : <div className="gt-layout">
      <div className="gt-list-wrap"><ol className="gt-list">{calls.map((call) => <li key={call.workflowRunModelCallId}><button type="button" className="gt-call" data-selected={selected === call.workflowRunModelCallId || undefined} onClick={() => { setError(null); setCallTab("result"); if (selected !== call.workflowRunModelCallId) { setDetail(null); setDetailLoading(true); setSelected(call.workflowRunModelCallId); } }}>
        <span className="gt-call-main"><strong>{call.purpose}</strong><span className="gt-adapter">{call.requestedModel ?? "model unobserved"}</span></span>
        <span className="gt-call-meta"><span>{call.captureCompleteness}</span><span>{call.captureSource}</span><span>Call #{call.callOrdinal}</span><time dateTime={call.createdAt}>{new Date(call.createdAt).toLocaleString()}</time></span>
      </button></li>)}</ol>{cursor && <div className="gt-paging"><button type="button" disabled={loadingOlder} onClick={() => void loadOlder()}>{loadingOlder ? "Loading…" : "Load older"}</button></div>}</div>
      {!selected ? <div className="gt-detail gt-empty">Select a call to read bounded metadata.</div> : detailLoading ? <div className="gt-detail gt-empty">Loading selected call…</div> : !detail ? <div className="gt-detail gt-empty">Selected model call is unavailable.</div> : <section className="gt-detail"><div className="gt-detail-head"><div><span className="gt-eyebrow">Selected model call</span><h3>{detail.purpose}</h3></div><span className="gt-state">{detail.captureCompleteness}</span></div>
        <div className="gt-fields"><Field label="Stable call id" value={detail.workflowRunModelCallId} /><Field label="Requested route" value={[detail.requestedProvider, detail.requestedModel].filter(Boolean).join(" · ") || "Auto / unobserved"} /><Field label="Capture" value={`${detail.captureCompleteness} · ${detail.captureSource}`} /><Field label="Node / cell" value={`${detail.nodeId ?? "run"}${detail.iterationKey ? ` · ${detail.iterationKey}` : ""}`} /><Field label="Attempts" value={String(detail.attempts.length)} /></div>
        <div className="gt-tabs" role="tablist" aria-label="Selected model-call content">{(["result", "prompt", "usage", "trace"] as const).map((tab) => <button type="button" role="tab" aria-selected={callTab === tab} key={tab} onClick={() => setCallTab(tab)}>{tab}</button>)}</div>
        <WorkflowRunStableModelCallContent metadata={detail} tab={callTab} />
      </section>}
    </div>}
  </section>;
}

function Field({ label, value }: { label: string; value: string }) {
  return <div className="gt-field"><span className="gt-field-label">{label}</span><span className="gt-field-value gt-mono">{value}</span></div>;
}
