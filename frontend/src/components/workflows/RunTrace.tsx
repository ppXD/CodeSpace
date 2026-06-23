import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunRecordView } from "@/api/workflows";
import { useRunRecords } from "@/hooks/use-workflows";

import { JsonView } from "./JsonView";

/**
 * The Trace tab — the run's RAW append-only event ledger (GET /records): the audit truth beside the Activity
 * narrative. ONE chronological row per record (EVERY type, unfiltered — the lifecycle/log/scope rows the narrative
 * drops are here too), each showing the raw RecordType + node + time; a record with a non-trivial payload expands in
 * place to its raw JSON. Source-agnostic — the RecordType is an OPEN string rendered verbatim, never switched on; only
 * a failure tone is derived for scanning. Polls in lockstep with the run.
 */
export function RunTrace({ runId }: { runId: string }) {
  const records = useRunRecords(runId);
  const rows = records.data?.records ?? [];

  if (rows.length === 0) {
    return <div className="run-trace-empty">{records.isLoading ? "Loading the event ledger…" : "No records yet."}</div>;
  }

  return (
    <div className="run-trace">
      <div className="run-trace-head"><Ic.Code size={12} aria-hidden="true" /> Event ledger · {rows.length} records</div>
      <ol className="run-trace-list">
        {rows.map((r) => <TraceRow key={r.sequence} record={r} />)}
      </ol>
    </div>
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
