import { useContext, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunAttempt, WorkflowRunStatus } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

import { RerunProvenanceContext } from "./rerunProvenanceContext";
import { nodeReruns } from "./runRerunProvenance";

/**
 * A node's own rerun history, shown AT the node — a `reran ·N` badge that opens a popover listing the attempts that
 * re-ran this node (oldest → newest, with each attempt's outcome). All those attempts belong to the same root run, so
 * the node carries its history without splitting the detail into separate run pages. Renders nothing for a node that
 * was never re-run (or outside a lineage context).
 *
 * The popover is PORTALED to the body with fixed coords from the badge — on the Canvas it lives inside React Flow's
 * `overflow:hidden` viewport, which would otherwise clip it.
 */
export function NodeRerunBadge({ nodeId, className }: { nodeId: string; className?: string }) {
  const prov = useContext(RerunProvenanceContext);
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      const t = e.target as Node;
      if (btnRef.current?.contains(t) || popRef.current?.contains(t)) return;
      setOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [open]);

  const reruns = prov ? nodeReruns(prov.rerunsByNode as Map<string, RunAttempt[]>, nodeId) : [];
  if (reruns.length === 0) return null;

  const toggle = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 5, left: r.left });
    }
    setOpen((o) => !o);
  };

  return (
    <div className={`node-rerun nodrag nopan${className ? ` ${className}` : ""}`}>
      <button
        ref={btnRef}
        type="button"
        className="node-rerun-badge"
        aria-expanded={open}
        title={`Re-ran ${reruns.length} time${reruns.length === 1 ? "" : "s"} — click for this node's rerun history`}
        onClick={toggle}
      >
        <Ic.Branch size={10} aria-hidden="true" /> reran ·{reruns.length}
      </button>
      {open && pos && createPortal(
        <div className="node-rerun-pop" role="dialog" ref={popRef} style={{ position: "fixed", top: pos.top, left: pos.left }} onClick={(e) => e.stopPropagation()}>
          <div className="node-rerun-pop-head">{nodeId} · rerun history</div>
          {reruns.map((a) => (
            <div className="node-rerun-row" key={a.runId}>
              <AttemptGlyph status={a.status} />
              <span className="node-rerun-row-n">Attempt {a.attemptNumber}</span>
              <span className="node-rerun-row-st" data-tone={tone(a.status)}>{a.status}</span>
              <span className="node-rerun-row-when">{relativeTime(a.createdDate)}</span>
            </div>
          ))}
        </div>,
        document.body,
      )}
    </div>
  );
}

function tone(status: WorkflowRunStatus): string {
  if (status === "Success") return "success";
  if (status === "Failure" || status === "Cancelled") return "failed";
  return "live";
}

function AttemptGlyph({ status }: { status: WorkflowRunStatus }) {
  const t = tone(status);
  if (t === "success") return <span className="node-rerun-glyph" data-tone="success"><Ic.Check size={12} aria-hidden="true" /></span>;
  if (t === "failed") return <span className="node-rerun-glyph" data-tone="failed"><Ic.X size={12} aria-hidden="true" /></span>;
  return <span className="node-rerun-glyph" data-tone="live"><Ic.Clock size={12} aria-hidden="true" /></span>;
}
