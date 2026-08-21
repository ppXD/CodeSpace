import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunRecordView, WorkflowRunDataCompletenessView } from "@/api/workflows";
import { useRunDataCompleteness, useRunRecordWindow } from "@/hooks/use-workflows";

import { JsonView } from "./JsonView";

/**
 * The Trace tab — a bounded window over the run's RAW append-only event ledger (GET /records/page): the audit truth beside the Activity
 * narrative. ONE chronological row per record (EVERY type, unfiltered — the lifecycle/log/scope rows the narrative
 * drops are here too), each showing the raw RecordType + node + time; a record with a non-trivial payload expands in
 * place to its raw JSON. Source-agnostic — the RecordType is an OPEN string rendered verbatim, never switched on; only
 * a failure tone is derived for scanning. The latest window polls while the run is active; older pages are explicit.
 */
export function RunTrace({ runId, active = false }: { runId: string; active?: boolean }) {
  const records = useRunRecordWindow(runId);
  const completeness = useRunDataCompleteness(runId, active);
  const rows = records.records;

  return (
    <div className="run-trace">
      <RunDataCompleteness view={completeness.data} loading={completeness.isLoading} failed={completeness.error != null} />
      {records.olderRecordsOmitted && (
        <div className="run-data-completeness-warning">
          Earlier records are outside this bounded window.
          {records.hasOlder && <>{" "}<button type="button" onClick={() => void records.loadOlder()} disabled={records.isLoadingOlder}>{records.isLoadingOlder ? "Loading older…" : "Load older"}</button></>}
        </div>
      )}
      {records.newerRecordsOmitted && (
        <div className="run-data-completeness-warning">
          Newer records are outside this historical window.
          {" "}<button type="button" onClick={records.returnToLatest}>Return to latest</button>
        </div>
      )}
      {records.error != null && rows.length > 0 && <div className="run-data-completeness-warning">Last refresh failed; displaying the last valid bounded window.</div>}
      {rows.length === 0 ? (
        <div className="run-trace-empty">{records.isLoading ? "Loading the event ledger…" : records.error != null ? "Couldn't load the event ledger." : "No records yet."}</div>
      ) : (
        <>
          <div className="run-trace-head"><Ic.Code size={12} aria-hidden="true" /> Event ledger · showing {rows.length} records</div>
          <ol className="run-trace-list">
            {rows.map((r) => <TraceRow key={r.sequence} record={r} />)}
          </ol>
        </>
      )}
    </div>
  );
}

function RunDataCompleteness({ view, loading, failed }: { view: WorkflowRunDataCompletenessView | null | undefined; loading: boolean; failed: boolean }) {
  return (
    <section className="run-data-completeness" aria-label="Recorded Workflow Run data completeness">
      <div className="run-data-completeness-head">Data completeness</div>
      <div className="run-data-completeness-scope">Recorded facets only · omitted facets remain indeterminate · no run-wide verdict.</div>
      {loading && view === undefined ? (
        <div className="run-data-completeness-empty">Loading producer statements…</div>
      ) : view == null ? (
        <div className="run-data-completeness-empty">Completeness metadata unavailable.</div>
      ) : view.facets.length === 0 ? (
        <div className="run-data-completeness-empty">No producer has stated completeness for this run.</div>
      ) : (
        <ul className="run-data-completeness-list">
          {view.facets.map((facet) => (
            <li key={facet.facet} className="run-data-completeness-row" data-readable={facet.isStrictlyReadable || undefined}>
              <code>{facet.facet}</code>
              <span className="run-data-completeness-count">{facet.presentRecordCount} present {facet.expectedRecordCount == null ? "· expected unstated" : `/ ${facet.expectedRecordCount} expected`}{facet.knownMissingCount > 0 ? ` · ${facet.knownMissingCount} known missing` : ""}</span>
              <span className="run-data-completeness-verdict">{facet.verdict}</span>
            </li>
          ))}
        </ul>
      )}
      {failed && view != null && <div className="run-data-completeness-warning">Last refresh failed; displaying the last valid producer statements.</div>}
      {view?.truncated && <div className="run-data-completeness-warning">Additional recorded facets were omitted by the bounded metadata read.</div>}
    </section>
  );
}

function TraceRow({ record }: { record: RunRecordView }) {
  const [open, setOpen] = useState(false);
  const payload = parsePayload(record.payloadJson);
  const expandable = payload !== null;

  // Only an expandable row is an interactive button; a flat (empty-payload) row is a plain div so a keyboard user
  // doesn't tab onto a focusable control that announces as a button but does nothing.
  const content = (
    <>
      <span className="run-trace-time">{new Date(record.occurredAt).toLocaleTimeString()}</span>
      <span className="run-trace-type">{record.recordType}</span>
      {record.nodeId && <span className="run-trace-node">{record.nodeId}</span>}
      {expandable && <span className="run-trace-caret" aria-hidden="true"><Ic.ChevronDown size={12} /></span>}
    </>
  );

  return (
    <li className="run-trace-row" data-tone={toneFor(record.recordType)} data-open={open || undefined}>
      {expandable ? (
        <button type="button" className="run-trace-bar" data-expandable aria-expanded={open} onClick={() => setOpen((v) => !v)}>
          {content}
        </button>
      ) : (
        <div className="run-trace-bar">{content}</div>
      )}
      {open && expandable && <div className="run-trace-payload"><JsonView data={payload} /></div>}
    </li>
  );
}

/**
 * Parse a record's raw payload for display — returns the parsed value ONLY for a non-empty object/array (the thing
 * worth an expand row); null for "{}" / an empty object or array / a bare scalar / unparseable input, so the row stays
 * flat. (jsonb payloads are objects in practice; the scalar + unparseable guards are defensive.)
 */
function parsePayload(payloadJson: string): unknown {
  if (!payloadJson || payloadJson === "{}") return null;

  try {
    const value = JSON.parse(payloadJson);

    if (value === null || typeof value !== "object") return null;   // a bare scalar / null isn't worth an expand row

    return Object.keys(value).length === 0 ? null : value;          // empty object / array → flat
  } catch {
    return null;
  }
}

/** A subtle row tone — only failures/cancellations stand out; everything else stays neutral (raw audit, not a story). */
function toneFor(recordType: string): "error" | undefined {
  return /fail|cancel/i.test(recordType) ? "error" : undefined;
}
