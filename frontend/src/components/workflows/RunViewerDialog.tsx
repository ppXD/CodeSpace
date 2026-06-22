import { useEffect, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import { RunDetailView } from "./RunDetailView";

/**
 * In-page run viewer — shows one run's live detail (the shared {@link RunDetailView}) in a
 * modal over the editor, so the operator never leaves the canvas. Rendered IN-TREE (not
 * portaled to <body>) so it stays inside `.acs-root` and the run-detail's `.wf-*` styles apply.
 *
 * Holds a small drill stack: a sub-workflow node can open its child run in place (Back returns to the
 * parent), so drilling into nested runs never leaves the viewer. Reset when the host opens a new run.
 */
export function RunViewerDialog({ runId, onClose }: { runId: string; onClose: () => void }) {
  const [stack, setStack] = useState<string[]>([runId]);
  const [topRun, setTopRun] = useState(runId);
  if (runId !== topRun) {
    // Host opened a different top-level run — reset the drill stack. React's "adjust state during render"
    // pattern: cheaper and safer than a setState-in-effect (no cascading-render) or a caller-supplied key.
    setTopRun(runId);
    setStack([runId]);
  }

  const current = stack[stack.length - 1];
  const canBack = stack.length > 1;

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
          {canBack && (
            <button className="mdl-back" onClick={() => setStack((s) => s.slice(0, -1))} title="Back to the parent run">
              <Ic.ChevronLeft size={16} />
            </button>
          )}
          <div className="mdl-title-wrap">
            <div className="mdl-title">Run {current.slice(0, 8)}</div>
            <div className="mdl-sub">{canBack ? "Sub-workflow run — Back returns to the parent." : "Live — refreshes while the run is in progress."}</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          <RunDetailView runId={current} onOpenRun={(childId) => setStack((s) => [...s, childId])} />
        </div>
      </div>
    </>
  );
}
