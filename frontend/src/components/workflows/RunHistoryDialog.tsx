import { useEffect } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useWorkflowRuns } from "@/hooks/use-workflows";

import { RunStatusBadge } from "./RunDetailView";

/**
 * In-page run history — lists this workflow's recent runs in a modal over the editor; picking
 * one opens the shared run viewer. Stays on the canvas (no navigation). Rendered in-tree so
 * the `.acs-root` styles apply, same as {@link RunViewerDialog}.
 */
export function RunHistoryDialog({ workflowId, onPick, onClose }: { workflowId: string; onPick: (runId: string) => void; onClose: () => void }) {
  const runs = useWorkflowRuns(workflowId);
  const rows = runs.data ?? [];

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl wf-run-modal" role="dialog" aria-modal="true">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Activity</div>
            <div className="mdl-sub">Recent runs of this workflow — click one to view its detail.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          {runs.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

          {!runs.isLoading && rows.length === 0 && (
            <div className="ct-empty">
              <div className="ct-empty-h">No activity yet</div>
              <div className="ct-empty-p">Click <strong>Run</strong> to start one.</div>
            </div>
          )}

          {rows.length > 0 && (
            <ul className="wf-run-hist">
              {rows.map((r) => (
                <li key={r.id} className="wf-run-hist-row" onClick={() => onPick(r.id)} title={`Run ${r.id.slice(0, 8)}`}>
                  <RunStatusBadge status={r.status} />
                  <span className="wf-run-hist-id">{r.id.slice(0, 8)}</span>
                  <span className="wf-run-hist-src">{r.sourceType}</span>
                  <span className="wf-run-hist-time">{r.startedAt ? new Date(r.startedAt).toLocaleString() : "—"}</span>
                  <span className="wf-run-hist-ver">v{r.workflowVersion}</span>
                  <Ic.ChevronRight size={12} />
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </>
  );
}
