import { useContext, useEffect, useRef, useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useAlert, useConfirm } from "@/components/dialog";
import { useRerunFromNode, useRerunMapBranch, useRerunMapBranches, useReplayRun } from "@/hooks/use-workflows";

import { RunActionsContext } from "./runActionsContext";
import { RunOpenContext } from "./runOpenContext";

/**
 * What a surface offers to rerun — the ONE thing every rerun affordance maps to. `mapItem` is a top-level flow.map
 * fan-out item (rerun this item / all failed items); `node` is any other failed step (rerun from here). Either way
 * the whole run can be replayed. The control turns this into the split button + dropdown.
 */
export type RerunTarget =
  // A flow.map fan-out. With `focusedIndex` the primary is "Rerun item" (that item); without it (the phase-level
  // bulk surface) the primary is "Rerun N failed items".
  | { kind: "mapItem"; mapNodeId: string; focusedIndex?: number; failedIndices: readonly number[]; totalCount: number }
  | { kind: "node"; nodeId: string; label?: string };

interface RerunOption {
  id: string;
  /** Dropdown label (specific, e.g. "Rerun item #0"). */
  label: string;
  /** Primary-button label (short, e.g. "Rerun item"); defaults to `label`. */
  primaryLabel?: string;
  icon: ReactNode;
  suggested?: boolean;
  current?: boolean;
  confirmTitle: string;
  confirmMessage: ReactNode;
  mutate: () => Promise<{ runId: string }>;
}

/**
 * The unified, context-aware Rerun control — a split button (primary action + caret) with a dropdown of every rerun
 * an operator can take from this surface, matching the design: the current action is checkmarked, the bulk
 * "Rerun all failed items" carries a "suggested" badge, and "Rerun entire run" is always available. Each option
 * confirms with an adaptive summary, forks a run, and navigates to it; any failure surfaces an alert (a concurrent
 * overlap is the 409 the active-rerun lease raises).
 *
 * Renders NOTHING unless the run is TERMINAL — a context-only gate, so the inner actions (which need the dialog +
 * query providers) mount only when a rerun is actually offered.
 */
export function RerunMenu({ target, className, compact, bare }: { target: RerunTarget; className?: string; compact?: boolean; bare?: boolean }) {
  const actions = useContext(RunActionsContext);
  if (!actions || !actions.isTerminal) return null;
  return <RerunMenuInner target={target} runId={actions.runId} className={className} compact={compact} bare={bare} />;
}

function RerunMenuInner({ target, runId, className, compact, bare }: { target: RerunTarget; runId: string; className?: string; compact?: boolean; bare?: boolean }) {
  const onOpenRun = useContext(RunOpenContext);
  const confirm = useConfirm();
  const alert = useAlert();
  const [menuOpen, setMenuOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  const rerunOne = useRerunMapBranch(runId);
  const rerunSet = useRerunMapBranches(runId);
  const fromNode = useRerunFromNode(runId);
  const replay = useReplayRun();
  const pending = rerunOne.isPending || rerunSet.isPending || fromNode.isPending || replay.isPending;

  useEffect(() => {
    if (!menuOpen) return;
    const onDoc = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [menuOpen]);

  const items = (n: number) => (n === 1 ? "item" : "items");
  const replayOption: RerunOption = {
    id: "run", label: "Rerun entire run", icon: <Ic.Play size={13} aria-hidden="true" />,
    confirmTitle: "Rerun the entire run?", confirmMessage: "Forks a fresh copy of the whole run from the start.",
    mutate: () => replay.mutateAsync(runId),
  };

  let options: RerunOption[];
  if (target.kind === "mapItem") {
    const { mapNodeId, focusedIndex, failedIndices, totalCount } = target;
    const failed = [...failedIndices].sort((a, b) => a - b);
    const allFailedOption = (current: boolean): RerunOption => {
      const reused = totalCount - failed.length;
      return {
        id: "allFailed", label: "Rerun all failed items", primaryLabel: `Rerun ${failed.length} failed ${items(failed.length)}`,
        suggested: !current, current, icon: <Ic.Branch size={13} aria-hidden="true" />,
        confirmTitle: `Rerun ${failed.length} failed ${items(failed.length)}?`,
        confirmMessage: `Rerun ${failed.length} failed ${items(failed.length)} (#${failed.join(", #")}). ${reused} ${items(reused)} will be reused.`,
        mutate: () => rerunSet.mutateAsync({ mapNodeId, branchIndices: failed, operationId: crypto.randomUUID() }),
      };
    };
    if (focusedIndex === undefined) {
      // Phase-level bulk surface — no single item is focused, so the bulk rerun is the primary action.
      if (failed.length === 0) return null;
      options = [allFailedOption(true), replayOption];
    } else {
      const reusedForItem = totalCount - 1;
      const stillBlocking = failed.filter((i) => i !== focusedIndex).length;
      const itemOption: RerunOption = {
        id: "item", label: `Rerun item #${focusedIndex}`, primaryLabel: "Rerun item", current: true,
        icon: <Ic.Branch size={13} aria-hidden="true" />,
        confirmTitle: `Rerun item #${focusedIndex}?`,
        confirmMessage: `Item #${focusedIndex} will rerun. ${reusedForItem} ${items(reusedForItem)} will be reused.`
          + (stillBlocking > 0 ? ` ${stillBlocking} other failed ${items(stillBlocking)} will still block the map.` : ""),
        mutate: () => rerunOne.mutateAsync({ mapNodeId, branchIndex: focusedIndex, operationId: crypto.randomUUID() }),
      };
      options = [itemOption, replayOption];
      if (failed.length >= 2) options.push(allFailedOption(false));
    }
  } else {
    options = [{
      id: "fromNode", label: "Rerun from here", current: true, icon: <Ic.Branch size={13} aria-hidden="true" />,
      confirmTitle: "Rerun from this step?",
      confirmMessage: "Forks a run that reuses everything upstream and re-runs this step and everything after it.",
      mutate: () => fromNode.mutateAsync({ fromNodeId: target.nodeId }),
    }, replayOption];
  }

  const navigate = (newRunId: string) => { setMenuOpen(false); onOpenRun?.(newRunId); };
  const onError = async (e: unknown) => {
    const conflict = e instanceof ApiError && e.status === 409;
    const message = e instanceof ApiError ? e.message : "Something went wrong starting the rerun. Please try again.";
    await alert({ title: conflict ? "Rerun already in progress" : "Couldn’t start the rerun", message });
  };
  const dispatch = async (opt: RerunOption) => {
    setMenuOpen(false);
    if (!(await confirm({ title: opt.confirmTitle, message: opt.confirmMessage, confirmLabel: opt.primaryLabel ?? opt.label }))) return;
    try { navigate((await opt.mutate()).runId); }
    catch (e) { await onError(e); }
  };

  const primary = options[0];
  const primaryText = primary.primaryLabel ?? primary.label;
  const iconOnly = compact || bare;
  return (
    <div className={`wf-rerun${compact ? " wf-rerun-compact" : ""}${bare ? " wf-rerun-bare" : ""}${className ? ` ${className}` : ""}`} ref={wrapRef}>
      <div className="wf-rerun-split">
        <button className="wf-rerun-primary" disabled={pending} title={iconOnly ? primaryText : undefined} aria-label={iconOnly ? primaryText : undefined} onClick={() => void dispatch(primary)}>
          <Ic.Play size={11} aria-hidden="true" /> {iconOnly ? null : (pending ? "Rerunning…" : primaryText)}
        </button>
        {!bare && (
          <button className="wf-rerun-caret" disabled={pending} aria-label="More rerun options" aria-expanded={menuOpen} onClick={() => setMenuOpen((o) => !o)}>
            <Ic.ChevronDown size={12} aria-hidden="true" />
          </button>
        )}
      </div>
      {!bare && menuOpen && (
        <div className="wf-rerun-menu" role="menu">
          {options.map((opt) => (
            <button key={opt.id} role="menuitem" className="wf-rerun-menu-item" disabled={pending} onClick={() => void dispatch(opt)}>
              <span className="wf-rerun-menu-ic">{opt.icon}</span>
              <span className="wf-rerun-menu-label">{opt.label}</span>
              {opt.suggested && <span className="wf-rerun-menu-sug">suggested</span>}
              {opt.current && <Ic.Check size={13} className="wf-rerun-menu-check" aria-hidden="true" />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
