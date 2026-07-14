import { useMemo, useState, type PointerEvent as ReactPointerEvent } from "react";

import { RunCanvas } from "@/components/workflows/RunCanvas";
import { RunStatusBadge } from "@/components/workflows/RunStatusBadge";
import { RunTrace } from "@/components/workflows/RunTrace";
import { useNodeManifests, useWorkflowRun } from "@/hooks/use-workflows";

/** Which surface the companion pane shows — the canvas (execution graph), the change set, or the raw ledger. */
export type PaneView = "canvas" | "changes" | "trace";

/** The three mini-tabs, in strip order. Kept as one source of truth so the strip + the body switch never drift. */
const PANE_TABS: readonly [PaneView, string][] = [["canvas", "Canvas"], ["changes", "Changes"], ["trace", "Trace"]];

/**
 * The run companion pane — a right-docked panel inside the Session Room that mounts the run's live
 * {@link RunCanvas} (the version-pinned execution graph), summoned per turn from that turn's execution
 * card. The Claude-artifacts / ChatGPT-canvas pattern: the canvas travels WITH the conversation instead
 * of taking over in a modal, so the chat column stays live beside it.
 *
 * D5 makes the D1 tab strip functional: three mini-tabs — Canvas (the canvas, unchanged) / Changes (the change
 * set, an honest coming-soon placeholder until its projector ships) / Trace (the run's raw append-only
 * {@link RunTrace} ledger). The `view` is controlled (SessionRoomView drives it from `?pane=` for deep-links);
 * when the caller omits `view`/`onViewChange` the pane manages it internally, defaulting to Canvas. RunCanvas's
 * props are assembled exactly as RunDetailView does (useWorkflowRun for the pinned definition + nodes + status,
 * the node manifests for the icons) so the pane and the full-page run canvas never drift; `onOpenRun` is threaded
 * through unchanged, so a sub-workflow drill still uses the existing full-page/modal path.
 */
export function RoomRunPane({ runId, turn, view, mode, onToggleBind, jumpToLatest, onJumpToLatest, onViewChange, onClose, onOpenRun, onGripPointerDown, focusNodeId }: {
  runId: string;
  /** The turn number this pane is bound to (the EFFECTIVE turn — latest in follow mode, the pinned one in pinned mode). The title reads "Canvas · Turn {N}". */
  turn: number;
  /** The active mini-tab, controlled by the URL via SessionRoomView. Omit to manage it internally (defaults to Canvas). */
  view?: PaneView;
  /** The D2 follow/pin binding mode — drives the header toggle label + the "Following" title hint. Omit to hide the toggle (standalone / test). */
  mode?: "follow" | "pinned";
  /** Fires when the follow/pin toggle is clicked — SessionRoomView flips the binding (pinned↔follow). Omit to hide the toggle. */
  onToggleBind?: () => void;
  /** When the pane is pinned behind a live newer turn, the latest turn number M → renders the "Latest: Turn M Running →" jump chip. Null/undefined hides it. */
  jumpToLatest?: number | null;
  /** Fires when the jump-to-latest chip is clicked — SessionRoomView switches the binding to follow. */
  onJumpToLatest?: () => void;
  /** Fires when a mini-tab is picked — SessionRoomView routes this into `?pane=`. Omit for internal-only switching. */
  onViewChange?: (view: PaneView) => void;
  onClose: () => void;
  /** Sub-workflow drill from a node in the canvas — kept on the existing path (do NOT change in D1). */
  onOpenRun?: (runId: string) => void;
  /** Wired to the header's ⋮⋮ grip so dragging it resizes the split (same handler as the column divider). */
  onGripPointerDown?: (e: ReactPointerEvent) => void;
  /** D3 forward jump: the canvas node to center + ring (the `?node=` deep-link a journal jump set). Only the Canvas tab
   *  consumes it; the other tabs ignore it. Undefined leaves the canvas at fitView. */
  focusNodeId?: string;
}) {
  const run = useWorkflowRun(runId);
  // The canvas paints the run's OWN version-pinned definition snapshot (run.definition), never the workflow's
  // current graph, so it stays faithful to how the run ran. Manifests drive the node icons/kinds. Assembled
  // exactly as RunDetailView does — replicated (three lines) rather than a shared hook, which would couple the
  // two views for no gain.
  const manifests = useNodeManifests();
  const manifestByType = useMemo(() => new Map((manifests.data ?? []).map((m) => [m.typeKey, m])), [manifests.data]);

  // Controlled/uncontrolled hybrid: a caller that drives `view` (SessionRoomView, from the URL) wins; otherwise the
  // pane keeps its own state so tab-switching works standalone (and in unit tests) without URL wiring.
  const [internalView, setInternalView] = useState<PaneView>(view ?? "canvas");
  const activeView = view ?? internalView;
  const selectView = (next: PaneView) => (onViewChange ? onViewChange(next) : setInternalView(next));

  const r = run.data;

  return (
    <aside className="room-pane" aria-label={`Execution canvas for turn ${turn}`}>
      {/* D2 pane follow/pin — the title carries a Following hint in follow mode; the toggle flips follow↔pin; the jump chip
          (pinned behind a live newer turn) rebinds to follow. All are hidden when `mode` isn't supplied (standalone). */}
      <div className="room-pane-head">
        {onGripPointerDown && (
          <span className="room-pane-grip" onPointerDown={onGripPointerDown} title="Drag to resize" aria-hidden="true">⋮⋮</span>
        )}
        <button className="room-pane-back" onClick={onClose} title="Back to conversation" aria-label="Back to conversation">‹</button>
        <span className="room-pane-title">Canvas · Turn {turn}</span>
        {mode === "follow" && <span className="room-pane-follow-hint">Following</span>}
        {mode && onToggleBind && (
          <button
            className="room-pane-bind"
            data-mode={mode}
            onClick={onToggleBind}
            title={mode === "follow" ? "Following the latest turn — click to pin this one" : "Pinned to this turn — click to follow the latest"}
          >
            {mode === "follow" ? "📍 Follow latest" : "📌 Pinned"}
          </button>
        )}
        {jumpToLatest != null && (
          <button className="room-pane-jump" onClick={onJumpToLatest} title="Switch to the latest turn's canvas">
            Latest: Turn {jumpToLatest} Running →
          </button>
        )}
        {r && <RunStatusBadge status={r.status} />}
        <button className="room-pane-close" onClick={onClose} title="Close canvas" aria-label="Close canvas">✕</button>
      </div>

      {/* D5 pane mini-tabs — Canvas / Changes / Trace, the D1 strip made real. Each is a tab button; the active one tracks `view`. */}
      <div className="room-pane-tabs" role="tablist" aria-label="Canvas views">
        {PANE_TABS.map(([key, label]) => (
          <button
            key={key}
            type="button"
            role="tab"
            className="room-pane-tab"
            data-active={activeView === key || undefined}
            aria-selected={activeView === key}
            onClick={() => selectView(key)}
          >
            {label}
          </button>
        ))}
      </div>

      {activeView === "canvas" ? (
        <div className="room-pane-body room-pane-canvas">
          {run.isLoading ? (
            <div className="room-pane-empty">Loading execution graph…</div>
          ) : !r ? (
            <div className="room-pane-empty">Execution record not found.</div>
          ) : r.definition ? (
            <RunCanvas definition={r.definition} runNodes={r.nodes} runStatus={r.status} manifestByType={manifestByType} runId={runId} onOpenRun={onOpenRun} focusNodeId={focusNodeId} />
          ) : (
            <div className="room-pane-empty">This run has no flow snapshot to show.</div>
          )}
        </div>
      ) : activeView === "trace" ? (
        <div className="room-pane-body room-pane-scroll">
          <RunTrace runId={runId} />
        </div>
      ) : (
        <div className="room-pane-body room-pane-scroll">
          <PaneComingSoon title="Changes" note="The files this run added or changed — the per-repository change set, diffs, and any PR it opened. This appears here once the changes projection ships." />
        </div>
      )}
    </aside>
  );
}

/**
 * An honest placeholder for the Changes tab, whose change-set projector hasn't shipped yet. Keeps the tab visible so the
 * pane reads as the intended whole, while being explicit the data is coming — never a fake-empty panel. Mirrors
 * RunDetailView's `RunTabComingSoon` copy, scoped to the pane so the pane doesn't pull in the whole run-detail module.
 */
function PaneComingSoon({ title, note }: { title: string; note: string }) {
  return (
    <div className="room-pane-coming">
      <span className="room-pane-coming-tag">Coming soon</span>
      <div className="room-pane-coming-h">{title}</div>
      <p className="room-pane-coming-p">{note}</p>
    </div>
  );
}
