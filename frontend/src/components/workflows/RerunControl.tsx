import { useContext, useEffect, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useAlert, useConfirm } from "@/components/dialog";
import { useRerunMapBranch, useRerunMapBranches, useReplayRun } from "@/hooks/use-workflows";

import { MAP_CONTAINER_KIND, parseIterationKey, type FanBranch } from "./mapBranches";
import { RunActionsContext } from "./runActionsContext";
import { RunOpenContext } from "./runOpenContext";

/**
 * The Rerun control on a fan-out card's focused item — a split button whose primary action adapts: "Rerun item"
 * for the focused item, or "Rerun N failed items" when several failed. The dropdown holds the alternatives
 * ("Rerun only this item", "Rerun entire run"). Each action confirms with an adaptive summary, then forks a run
 * and navigates to it. A concurrent rerun of the same item is refused (409) by the backend lease → a friendly alert.
 *
 * This GATE renders NOTHING unless it's a TOP-LEVEL flow.map item (single-segment key + map container) on a
 * TERMINAL run — so a nested/loop body, or a still-live run, shows no rerun affordance (matching the backend's
 * gates). It uses ONLY `useContext` (safe without providers), so the inner actions component — which needs the
 * dialog + query providers the app supplies — mounts only when a rerun is actually offered.
 */
export function RerunControl({ branches, focused }: { branches: readonly FanBranch[]; focused: FanBranch }) {
  const actions = useContext(RunActionsContext);
  const segments = parseIterationKey(focused.row.iterationKey);
  if (!actions || !actions.isTerminal || segments.length !== 1 || focused.row.containerKind !== MAP_CONTAINER_KIND) return null;

  return <RerunActions branches={branches} focused={focused} runId={actions.runId} mapNodeId={segments[0].containerId} />;
}

function RerunActions({ branches, focused, runId, mapNodeId }: { branches: readonly FanBranch[]; focused: FanBranch; runId: string; mapNodeId: string }) {
  const onOpenRun = useContext(RunOpenContext);
  const confirm = useConfirm();
  const alert = useAlert();
  const [menuOpen, setMenuOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  const rerunOne = useRerunMapBranch(runId);
  const rerunSet = useRerunMapBranches(runId);
  const replay = useReplayRun();
  const pending = rerunOne.isPending || rerunSet.isPending || replay.isPending;

  useEffect(() => {
    if (!menuOpen) return;
    const onDoc = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [menuOpen]);

  const failed = branches.filter((b) => b.row.status === "Failure").map((b) => b.index).sort((a, b) => a - b);
  const succeeded = branches.filter((b) => b.row.status === "Success").length;
  const reused = branches.length - 1;
  const multiFailed = failed.length >= 2;
  const items = (n: number) => (n === 1 ? "item" : "items");

  const navigate = (newRunId: string) => {
    setMenuOpen(false);
    onOpenRun?.(newRunId);
  };
  const onError = async (e: unknown) => {
    if (e instanceof ApiError && e.status === 409) await alert({ title: "Rerun already in progress", message: e.message });
    else throw e;
  };

  const rerunItem = async (index: number) => {
    const message = succeeded > 0
      ? `Only item #${index} will rerun. ${reused} ${items(reused)} will be reused.`
      : `Item #${index} will rerun, but ${failed.length - 1} failed ${items(failed.length - 1)} will still block the map.`;
    if (!(await confirm({ title: `Rerun item #${index}?`, message, confirmLabel: "Rerun item" }))) return;
    try { navigate((await rerunOne.mutateAsync({ mapNodeId, branchIndex: index, operationId: crypto.randomUUID() })).runId); }
    catch (e) { await onError(e); }
  };
  const rerunFailed = async () => {
    const message = `Rerun ${failed.length} failed items (#${failed.join(", #")}). ${succeeded} successful ${items(succeeded)} will be reused.`;
    if (!(await confirm({ title: `Rerun ${failed.length} failed items?`, message, confirmLabel: "Rerun failed" }))) return;
    try { navigate((await rerunSet.mutateAsync({ mapNodeId, branchIndices: failed, operationId: crypto.randomUUID() })).runId); }
    catch (e) { await onError(e); }
  };
  const rerunRun = async () => {
    setMenuOpen(false);
    if (!(await confirm({ title: "Rerun the entire run?", message: "Forks a fresh copy of the whole run from the start.", confirmLabel: "Rerun run" }))) return;
    try { navigate((await replay.mutateAsync(runId)).runId); }
    catch (e) { await onError(e); }
  };

  const primary = multiFailed
    ? { label: `Rerun ${failed.length} failed items`, run: () => void rerunFailed() }
    : { label: "Rerun item", run: () => void rerunItem(focused.index) };

  return (
    <div className="wf-rf-rerun" ref={wrapRef}>
      <div className="wf-rf-rerun-split">
        <button className="wf-rf-rerun-primary" disabled={pending} onClick={primary.run}>
          <Ic.Play size={11} /> {pending ? "Rerunning…" : primary.label}
        </button>
        <button className="wf-rf-rerun-caret" disabled={pending} aria-label="More rerun options" aria-expanded={menuOpen} onClick={() => setMenuOpen((o) => !o)}>
          <Ic.ChevronDown size={12} />
        </button>
      </div>
      {menuOpen && (
        <div className="wf-rf-rerun-menu" role="menu">
          {multiFailed && (
            <button role="menuitem" onClick={() => void rerunItem(focused.index)}>Rerun only this item (#{focused.index})</button>
          )}
          <button role="menuitem" onClick={() => void rerunRun()}>Rerun entire run</button>
        </div>
      )}
    </div>
  );
}
