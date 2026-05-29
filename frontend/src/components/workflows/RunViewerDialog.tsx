import { useEffect } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import { RunDetailView } from "./RunDetailView";

/**
 * In-page run viewer — shows one run's live detail (the shared {@link RunDetailView}) in a
 * modal over the editor, so the operator never leaves the canvas. Rendered IN-TREE (not
 * portaled to <body>) so it stays inside `.acs-root` and the run-detail's `.wf-*` styles apply.
 */
export function RunViewerDialog({ runId, onClose }: { runId: string; onClose: () => void }) {
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
            <div className="mdl-title">Run {runId.slice(0, 8)}</div>
            <div className="mdl-sub">Live — refreshes while the run is in progress.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          <RunDetailView runId={runId} />
        </div>
      </div>
    </>
  );
}
