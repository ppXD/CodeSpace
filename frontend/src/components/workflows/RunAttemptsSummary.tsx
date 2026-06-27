import { useEffect, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunAttempt, WorkflowRunStatus } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

/**
 * The run's lineage at a glance — one informational "N attempts" pill (NOT a snapshot switcher). The detail always
 * shows the current state; this just lists, for the record, every attempt and which node it re-ran. Each node's own
 * history lives on the node (see NodeRerunBadge). Hidden for a never-rerun run.
 */
export function RunAttemptsSummary({ attempts }: { attempts: readonly RunAttempt[] }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [open]);

  if (attempts.length < 2) return null;

  return (
    <div className="run-attempts" ref={ref}>
      <button type="button" className="run-attempts-pill" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <Ic.Branch size={11} aria-hidden="true" /> {attempts.length} attempts
      </button>
      {open && (
        <div className="run-attempts-pop" role="dialog">
          <div className="run-attempts-pop-head">Every attempt belongs to this run</div>
          {attempts.map((a) => (
            <div className="run-attempts-row" key={a.runId}>
              <AttemptGlyph status={a.status} />
              <span className="run-attempts-row-n">Attempt {a.attemptNumber}</span>
              <span className="run-attempts-row-node">{a.rerunFromNodeId ? `reran ${a.rerunFromNodeId}` : "first run"}</span>
              <span className="run-attempts-row-when">{relativeTime(a.createdDate)}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function AttemptGlyph({ status }: { status: WorkflowRunStatus }) {
  if (status === "Success") return <span className="run-attempts-glyph" data-tone="success"><Ic.Check size={12} aria-hidden="true" /></span>;
  if (status === "Failure" || status === "Cancelled") return <span className="run-attempts-glyph" data-tone="failed"><Ic.X size={12} aria-hidden="true" /></span>;
  return <span className="run-attempts-glyph" data-tone="live"><Ic.Clock size={12} aria-hidden="true" /></span>;
}
