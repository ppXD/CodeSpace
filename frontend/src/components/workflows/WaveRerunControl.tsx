import { useContext, useEffect, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useAlert, useConfirm } from "@/components/dialog";
import { useRerunMapBranches, useReplayRun } from "@/hooks/use-workflows";

import { parseIterationKey } from "./mapBranches";
import { RunActionsContext } from "./runActionsContext";
import { RunOpenContext } from "./runOpenContext";
import { tileState, type AgentWave } from "./runActivity";

/**
 * The Activity-view rerun control for a flow.map "Fan out" phase — a thin action bar under the phase header offering
 * "Rerun N failed items" (forks one run over the failed items, reusing the rest) + "Rerun entire run". The Canvas's
 * RerunControl is per-item; here, where the failure reads at the phase level, the bulk actions are the natural fit.
 *
 * This GATE uses ONLY useContext (safe without providers) and renders nothing unless the wave is a flow.map fan-out
 * (kind "map") on a TERMINAL run with at least one failed item — so the inner actions (which need the dialog + query
 * providers) mount only when a rerun is actually offered.
 */
export function WaveRerunControl({ wave }: { wave: AgentWave }) {
  const actions = useContext(RunActionsContext);

  if (wave.kind !== "map" || !actions || !actions.isTerminal) return null;

  const failedIndices = wave.agents
    .filter((a) => tileState(a.status) === "failed")
    .map((a) => parseIterationKey(a.iterationKey ?? ""))
    .filter((segs) => segs.length === 1 && segs[0].containerId === wave.id)
    .map((segs) => segs[0].index)
    .sort((x, y) => x - y);

  if (failedIndices.length === 0) return null;

  return <WaveRerunActions runId={actions.runId} mapNodeId={wave.id} failedIndices={failedIndices} reusedCount={wave.agents.length - failedIndices.length} />;
}

function WaveRerunActions({ runId, mapNodeId, failedIndices, reusedCount }: { runId: string; mapNodeId: string; failedIndices: number[]; reusedCount: number }) {
  const onOpenRun = useContext(RunOpenContext);
  const confirm = useConfirm();
  const alert = useAlert();
  const [menuOpen, setMenuOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  const rerunSet = useRerunMapBranches(runId);
  const replay = useReplayRun();
  const pending = rerunSet.isPending || replay.isPending;
  const items = (n: number) => (n === 1 ? "item" : "items");

  useEffect(() => {
    if (!menuOpen) return;
    const onDoc = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [menuOpen]);

  const navigate = (newRunId: string) => { setMenuOpen(false); onOpenRun?.(newRunId); };
  const onError = async (e: unknown) => {
    const conflict = e instanceof ApiError && e.status === 409;
    const message = e instanceof ApiError ? e.message : "Something went wrong starting the rerun. Please try again.";
    await alert({ title: conflict ? "Rerun already in progress" : "Couldn’t start the rerun", message });
  };

  const rerunFailed = async () => {
    const n = failedIndices.length;
    const message = `Rerun ${n} failed ${items(n)} (#${failedIndices.join(", #")}). ${reusedCount} ${items(reusedCount)} will be reused.`;
    if (!(await confirm({ title: `Rerun ${n} failed ${items(n)}?`, message, confirmLabel: "Rerun failed" }))) return;
    try { navigate((await rerunSet.mutateAsync({ mapNodeId, branchIndices: failedIndices, operationId: crypto.randomUUID() })).runId); }
    catch (e) { await onError(e); }
  };
  const rerunRun = async () => {
    setMenuOpen(false);
    if (!(await confirm({ title: "Rerun the entire run?", message: "Forks a fresh copy of the whole run from the start.", confirmLabel: "Rerun run" }))) return;
    try { navigate((await replay.mutateAsync(runId)).runId); }
    catch (e) { await onError(e); }
  };

  const n = failedIndices.length;
  return (
    <div className="run-tl-rerun" ref={wrapRef}>
      <div className="run-tl-rerun-split">
        <button className="run-tl-rerun-primary" disabled={pending} onClick={() => void rerunFailed()}>
          <Ic.Play size={11} /> {pending ? "Rerunning…" : `Rerun ${n} failed ${items(n)}`}
        </button>
        <button className="run-tl-rerun-caret" disabled={pending} aria-label="More rerun options" aria-expanded={menuOpen} onClick={() => setMenuOpen((o) => !o)}>
          <Ic.ChevronDown size={12} />
        </button>
      </div>
      {menuOpen && (
        <div className="run-tl-rerun-menu" role="menu">
          <button role="menuitem" onClick={() => void rerunRun()}>Rerun entire run</button>
        </div>
      )}
    </div>
  );
}
