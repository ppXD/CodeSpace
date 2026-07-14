/* eslint-disable react-refresh/only-export-components -- the room view co-locates its pure pane-binding helpers (resolveBinding, resolvePaneFromTurn, journalStepNodeId, shouldShowJumpToLatest) with the component; fast-refresh granularity is moot for these. */
import { createContext, Fragment, type PointerEvent as ReactPointerEvent, type ReactNode, useCallback, useContext, useEffect, useRef, useState } from "react";
import { useNavigate } from "@tanstack/react-router";

import type {
  AgentGroupBlock,
  AssistantTurnBlock,
  DecisionBlock,
  DeliveryBlock,
  DiagnosticBlock,
  ExecutionMapStep,
  ExecutionStepStatus,
  FinalAnswerBlock,
  JournalAgentCard,
  JournalModelCall,
  JournalReviewVerdict,
  JournalStep,
  JournalSubtask,
  JournalTurn,
  JournalView,
  LiveActivityBlock,
  PlanChecklistBlock,
  PlanChecklistItem,
  RoomAction,
  RoomAgentCard,
  RoomBlock,
  RoomFilePreview,
  RoomPlanQuestion,
  RoomTurnAttempt,
  RoomView,
  StatBlock,
  StatItem,
} from "@/api/sessions";
import { sessionsApi } from "@/api/sessions";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { PendingDecision, PhaseAgentRef, WorkflowRunStatus } from "@/api/workflows";
import { AgentTerminal } from "@/components/workflows/AgentTerminal";
import { DecisionCard } from "@/components/workflows/DecisionCard";
import { RunActionsContext } from "@/components/workflows/runActionsContext";
import { RunOpenContext } from "@/components/workflows/runOpenContext";
import { decisionsForRun } from "@/components/workflows/runDecisions";
import { compactAge } from "@/components/workflows/cockpit";
import { formatTokens } from "@/components/workflows/runActivity";
import { planAgentStatus, planDepsLabel, planStateIcon, planStateTone, planStateWord, composePlanFeedback } from "@/lib/planChecklist";
import { useAlert, useConfirm } from "@/components/dialog";
import { LaunchTaskModal } from "@/components/tasks/LaunchTaskModal";
import { isRunActive, useCancelRun, useContinueRun, useOpenPullRequest, usePendingDecisions, useReplayRun } from "@/hooks/use-workflows";
import { statusWord } from "@/lib/runStatus";
import { shortRunTitle } from "@/lib/runTitle";
import { useRunRoomStream } from "@/hooks/use-run-room-stream";
import { useNowTick } from "@/hooks/use-now-tick";
import { liveRunSummary } from "./live-run-summary";
import { partitionForFailureHoist } from "./room-blocks";
import { RoomRunPane, type PaneView } from "./RoomRunPane";

/** What the right-side preview drawer is showing — an agent (its terminal) or a file (its content + download). */
type DrawerTarget =
  | { kind: "agent"; agent: RoomAgentCard; runId: string }
  | { kind: "file"; runId: string; path: string; agentRunId?: string }
  | { kind: "modelcall"; runId: string; sequence: number; call: JournalModelCall };

/** Open the unified preview drawer. Any row (an agent card, a changed file) calls this to preview on the right. */
const RoomDrawerContext = createContext<(t: DrawerTarget) => void>(() => {});
const useRoomDrawer = () => useContext(RoomDrawerContext);

/**
 * The Session Journal for this run — undefined only while its fetch resolves (the Room frame renders meanwhile). When
 * present, a turn REUSES the Room frame (header, execution map ①, plan checklist ②, result card ⑥, mono style) but
 * replaces its narrative blocks (agent groups / supervisor steps / the live ticker) with the journal's CHRONOLOGICAL
 * steps ③, so the journal is the Room with a better middle, not a different-looking page.
 */
const JournalContext = createContext<JournalView | null>(null);
const useJournal = () => useContext(JournalContext);

/**
 * D3 forward jump — a journal beat's "在Canvas查看" opens the companion pane on THIS turn's canvas, focused on a node.
 * The turn is captured once at the {@link AssistantTurn} level (which knows its turnIndex + runId), so a deep consumer
 * (a {@link JournalStepRow}) passes only the nodeId. Null when the turn has no runId (no canvas to open) — the
 * affordance then doesn't render, so a jump is never offered without a real target.
 */
const PaneNodeFocusContext = createContext<((nodeId: string) => void) | null>(null);
const usePaneNodeFocus = () => useContext(PaneNodeFocusContext);

/**
 * The canvas node a journal step maps to — the gate for its "在Canvas查看" jump. A step that carries no `nodeId` (a
 * supervisor decision with no workflow cell, a model call, plain lifecycle housekeeping) resolves to null, so no jump
 * affordance is rendered: we never fabricate a node target. Exported for the unit test that pins the no-id → no-affordance gate.
 */
export function journalStepNodeId(step: JournalStep): string | null {
  return step.nodeId ?? null;
}

/** The companion pane's bound run + turn, resolved from a turn number against the room's blocks. Pure so the
 *  split-state decision (summon → which run; URL-restore → which run; close → none) is unit-testable without
 *  rendering the heavy Room. Returns null when the turn is absent (or none is requested). */
export function resolvePaneFromTurn(blocks: RoomBlock[], turn: number | null): { runId: string; turn: number } | null {
  if (turn == null) return null;
  const t = blocks.find((b): b is AssistantTurnBlock => b.type === "assistant_turn" && b.turnIndex === turn);
  return t && t.runId ? { runId: t.runId, turn } : null;
}

/**
 * The pane's binding to the conversation — the D2 follow/pin state machine. Closed, or open in one of two modes:
 * "follow" tracks the LATEST turn (its effective turn is re-derived each render, so a new turn auto-rebinds the pane),
 * "pinned" freezes a specific turn (summoned from that turn's card, or restored from `?turn=N`). The effective run is
 * always derived from the effective turn via {@link resolvePaneFromTurn} — the binding never stores a runId.
 */
export type PaneBinding =
  | { open: false }
  | { open: true; mode: "follow"; view: PaneView; node?: string }
  | { open: true; mode: "pinned"; turn: number; view: PaneView; node?: string };

/** The turn the binding currently shows — the latest in follow mode, the pinned turn in pinned mode. Null when closed
 *  (or when follow mode has no turns yet). Kept pure + separate from the render so follow-rebind is testable. */
function bindingTurn(binding: PaneBinding, latestTurnIndex: number | null): number | null {
  if (!binding.open) return null;
  return binding.mode === "follow" ? latestTurnIndex : binding.turn;
}

/** The pane's effective { runId, turn, view }, resolved from the binding against the room's blocks + the latest turn.
 *  Follow → the latest turn's run; pinned → the pinned turn's run. Null when closed or the resolved turn is absent.
 *  Pure so the follow-rebind + pin decisions are unit-testable without rendering the heavy Room. */
export function resolveBinding(blocks: RoomBlock[], binding: PaneBinding, latestTurnIndex: number | null): { runId: string; turn: number; view: PaneView; node?: string } | null {
  if (!binding.open) return null;
  const p = resolvePaneFromTurn(blocks, bindingTurn(binding, latestTurnIndex));
  // `node` (the D3 canvas focus) rides only when set — kept off the object otherwise so a nodeless binding reads clean.
  return p ? { ...p, view: binding.view, ...(binding.node ? { node: binding.node } : {}) } : null;
}

/** Whether the "jump to latest" chip shows — the pane is PINNED to turn T while a NEWER turn than T is still ACTIVE
 *  (running). Hidden when following, when the latest isn't newer, or when the latest is already terminal. Pure. */
export function shouldShowJumpToLatest(binding: PaneBinding, latest: { turnIndex: number; status: WorkflowRunStatus } | null | undefined): boolean {
  if (!binding.open || binding.mode !== "pinned" || !latest) return false;
  return isRunActive(latest.status) && latest.turnIndex > binding.turn;
}

/** The initial binding from the URL-restore props: no view → closed; view + turn → pinned to that turn; view alone
 *  (no turn, or turn=latest) → follow. Shared by the seed + the prev-prop URL re-sync so both stay in lockstep. */
function initialBinding(view: PaneView | undefined, turn: number | null, node: string | null): PaneBinding {
  if (!view) return { open: false };
  const n = node ?? undefined;
  return turn != null ? { open: true, mode: "pinned", turn, view, node: n } : { open: true, mode: "follow", view, node: n };
}

/** The pane's URL-sync key — the binding folded to one string so a mini-tab switch (view changes, mode/turn don't)
 *  re-syncs from the URL exactly like a mode change. A closed pane collapses to "none". */
function bindingSyncKey(binding: PaneBinding): string {
  if (!binding.open) return "none";
  const base = binding.mode === "follow" ? `follow:${binding.view}` : `pin:${binding.turn}:${binding.view}`;
  return binding.node ? `${base}:${binding.node}` : base;   // a ?node change re-syncs from the URL like a mode/turn change
}

const PANE_FRAC_KEY = "codespace.room.pane-frac";
const DEFAULT_PANE_FRAC = 0.44;
const MIN_LEFT_PX = 480;
const MIN_PANE_PX = 360;

/**
 * The companion pane's width as a persisted FRACTION of the room split (0–1). Mirrors usePaneResize's
 * localStorage-backed, document-tracked drag, but stores a fraction (robust to window resizes) instead of
 * px, and enforces the chat column's min width by clamping the drag against the live container width — so
 * usePaneResize (px, palette/inspector-keyed) isn't a clean fit and this stays a small dedicated hook.
 */
function useRoomPaneFrac() {
  const [frac, setFrac] = useState<number>(() => {
    try { const raw = localStorage.getItem(PANE_FRAC_KEY); if (raw) { const f = parseFloat(raw); if (f >= 0.2 && f <= 0.7) return f; } } catch { /* unreadable / corrupt storage → default */ }
    return DEFAULT_PANE_FRAC;
  });

  useEffect(() => {
    try { localStorage.setItem(PANE_FRAC_KEY, String(frac)); } catch { /* storage may be unavailable */ }
  }, [frac]);

  const startResize = useCallback((container: HTMLElement | null, e: ReactPointerEvent) => {
    e.preventDefault();
    if (!container) return;
    const rect = container.getBoundingClientRect();

    const onMove = (ev: PointerEvent) => {
      // The pane grows dragging the divider LEFT. Clamp so the pane keeps a floor AND the chat column keeps its min.
      const paneW = Math.max(MIN_PANE_PX, Math.min(rect.width - MIN_LEFT_PX, rect.right - ev.clientX));
      setFrac(paneW / rect.width);
    };
    const onUp = () => {
      document.removeEventListener("pointermove", onMove);
      document.removeEventListener("pointerup", onUp);
      document.body.style.removeProperty("user-select");
      document.body.style.removeProperty("cursor");
    };

    document.body.style.userSelect = "none";
    document.body.style.cursor = "col-resize";
    document.addEventListener("pointermove", onMove);
    document.addEventListener("pointerup", onUp);
  }, []);

  return { frac, startResize };
}

/**
 * The Session room — run-detail rendered from the backend-authored Session Room projection (`RoomView`) as a
 * Claude/Codex-style WORK TRANSCRIPT, replicating the exported Session.dc.html 1:1: a session header (breadcrumb +
 * title + status + meta chips), then turns as a message stream — a right-aligned user bubble, then the AI's reply
 * (avatar + name + status pill + duration) flowing into a narrative lead, the canonical Plan→Work→Review→Deliver
 * execution map, collapsible Codex-style detail rows (Plan / Agents / Files changed / Tools / Reasoning), a PR card,
 * and — on failure — a humanized diagnostic with typed remediation. The frontend renders blocks purely by `type` and
 * owns no copy / order / status; an unknown block degrades to a faint line. The design palette IS the project warm
 * theme, but the design's deeper semantic colors (good/blue/err + bg/line variants) are scoped to `.room-room`.
 */
export function SessionRoomView({ teamSlug, room, journal, initialPaneTurn, initialPaneView, initialPaneNode, onPaneChange }: { teamSlug: string; room: RoomView; journal?: JournalView | null; initialPaneTurn?: number | null; initialPaneView?: PaneView; initialPaneNode?: string | null; onPaneChange?: (view: PaneView | null, turn: number | null, node: string | null) => void }) {
  const navigate = useNavigate();

  const nowMs = useNowTick();

  const openRun = (runId: string) => navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber: runId } });

  const splitRef = useRef<HTMLDivElement>(null);
  const { frac, startResize } = useRoomPaneFrac();

  // The latest turn drives BOTH the header's primary outcome status AND the pane's follow mode — a follow-bound pane
  // re-derives its effective turn from this each render, so a newly staged turn auto-rebinds the pane with no effect.
  const latestTurn = [...room.blocks].reverse().find((b): b is AssistantTurnBlock => b.type === "assistant_turn");

  // The run companion pane — a right-docked RunCanvas/trace/changes bound to the conversation (Claude-artifacts style)
  // via the D2 follow/pin state machine (see PaneBinding). Seeded from the URL (?pane={view}&turn=N → pinned; ?pane with
  // no turn → follow), and kept in lockstep with it (deep-link / back-forward) via the prev-prop pattern rather than an
  // effect, to avoid the cascading re-render setState-in-effect causes. The synced key folds the whole binding so
  // switching mini-tab (view changes, mode/turn don't) restores from the URL just like a mode change.
  const [binding, setBinding] = useState<PaneBinding>(() => initialBinding(initialPaneView, initialPaneTurn ?? null, initialPaneNode ?? null));
  const [syncedPaneKey, setSyncedPaneKey] = useState<string>(() => bindingSyncKey(initialBinding(initialPaneView, initialPaneTurn ?? null, initialPaneNode ?? null)));
  const nextBinding = initialBinding(initialPaneView, initialPaneTurn ?? null, initialPaneNode ?? null);
  const nextPaneKey = bindingSyncKey(nextBinding);
  if (nextPaneKey !== syncedPaneKey) {
    setSyncedPaneKey(nextPaneKey);
    setBinding(nextBinding);
  }

  // Effective { runId, turn, view } — follow → the latest turn, pinned → the pinned turn (both → that turn's run). Null
  // → the Room renders single-column. `showJump` gates the jump-to-latest chip (pinned behind a live newer turn).
  const effPane = resolveBinding(room.blocks, binding, latestTurn?.turnIndex ?? null);
  const showJump = shouldShowJumpToLatest(binding, latestTurn ?? null);

  // A binding change is pushed to the URL: pinned → ?pane={view}&turn={N}; follow → ?pane={view} (no turn); closed → clear.
  // The D3 canvas focus rides as ?node={id} when the binding carries one; every transition that isn't a node-jump drops it.
  const commitBinding = (next: PaneBinding) => {
    setBinding(next);
    setSyncedPaneKey(bindingSyncKey(next));
    if (!next.open) onPaneChange?.(null, null, null);
    else onPaneChange?.(next.view, next.mode === "pinned" ? next.turn : null, next.node ?? null);
  };

  // Summon from a turn's ⧉ Canvas card / footer action → PIN the pane to that turn (a deliberate "show me THIS turn"), on
  // the given mini-tab (canvas by default, trace when summoned from a failure diagnostic). No node → the whole-map view
  // (and clears any prior ?node focus, since a different turn is now in view).
  const summonPane = (turn: number, view: PaneView = "canvas") => commitBinding({ open: true, mode: "pinned", turn, view });
  // D3 forward jump from a journal beat → PIN the pane to that turn's canvas AND focus (center + ring) the given node.
  const summonPaneNode = (turn: number, node: string) => commitBinding({ open: true, mode: "pinned", turn, view: "canvas", node });
  const closePane = () => commitBinding({ open: false });

  // The follow/pin toggle: pinned → follow (rebinds to the latest turn); follow → pinned (freezes the turn shown NOW).
  // Either way the pane rebinds to a (possibly) different turn, so the pinned node focus is dropped.
  const toggleBind = () => {
    if (!binding.open) return;
    if (binding.mode === "pinned") { commitBinding({ open: true, mode: "follow", view: binding.view }); return; }
    const turn = latestTurn?.turnIndex;
    if (turn == null) return;
    commitBinding({ open: true, mode: "pinned", turn, view: binding.view });
  };

  // The jump-to-latest chip (shown only while pinned behind a live newer turn) → switch to follow (rebind to latest).
  const jumpToLatest = () => { if (binding.open) commitBinding({ open: true, mode: "follow", view: binding.view }); };

  // Switching mini-tab keeps the same turn, so the node focus is preserved (it re-applies when the Canvas tab is back).
  const changePaneView = (view: PaneView) => {
    if (!binding.open) return;
    commitBinding(binding.mode === "pinned" ? { open: true, mode: "pinned", turn: binding.turn, view, node: binding.node } : { open: true, mode: "follow", view, node: binding.node });
  };
  const onGripPointerDown = (e: ReactPointerEvent) => startResize(splitRef.current, e);

  const turnCount = room.blocks.filter((b) => b.type === "assistant_turn").length;
  const startedAt = room.blocks.map((b) => ("at" in b ? b.at : null)).find(Boolean) as string | undefined;

  // The header's PRIMARY status is the run OUTCOME from the latest turn (Working / Done / Failed / Waiting / Stopped),
  // NOT the session's Open/Closed — a failed run must never read "OPEN". Open/Closed stays a demoted cue (it gates the composer).
  const outcomeTone = latestTurn ? statusTone(latestTurn.status, isRunActive(latestTurn.status)) : null;
  const outcomeLabel = latestTurn ? pillLabel(latestTurn.status, isRunActive(latestTurn.status)) : null;

  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(room.title);
  // Reset the editable title when the session's title changes upstream (rename / switching session) — done during
  // render via the prev-prop pattern, not in an effect, to avoid the cascading re-render the setState-in-effect causes.
  const [syncedTitle, setSyncedTitle] = useState(room.title);
  if (room.title !== syncedTitle) { setSyncedTitle(room.title); setTitle(room.title); }

  const saveTitle = async () => {
    setEditing(false);
    const next = title.trim();
    if (!next || next === room.title) { setTitle(room.title); return; }
    try { await sessionsApi.renameSession(room.sessionId, next); } catch { setTitle(room.title); }
  };

  const [drawer, setDrawer] = useState<DrawerTarget | null>(null);

  return (
    <JournalContext.Provider value={journal ?? null}>
    <RoomDrawerContext.Provider value={setDrawer}>
    <section className="room-room" data-drawer={drawer ? true : undefined} data-pane={effPane ? true : undefined}>
      <header className="room-head">
        <div className="room-head-top">
          <nav className="room-crumbs">
            <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Runs</a>
            <span className="room-crumb-sep">/</span>
            <span className="room-crumb-cur" title={room.title}>{shortRunTitle(room.title)}</span>
          </nav>
          <div className="room-head-icons">
            <button className="room-icon-btn" title="Copy link to this session" onClick={() => navigator.clipboard?.writeText(window.location.href)}><Sym n="link" s={16} /></button>
          </div>
        </div>
        <div className="room-head-title">
          {editing ? (
            <input
              className="room-head-edit"
              value={title}
              autoFocus
              onChange={(e) => setTitle(e.target.value)}
              onBlur={saveTitle}
              onKeyDown={(e) => { if (e.key === "Enter") saveTitle(); else if (e.key === "Escape") { setTitle(room.title); setEditing(false); } }}
            />
          ) : (
            <h1 role="button" tabIndex={0} title="Click to rename" onClick={() => setEditing(true)}>{title}</h1>
          )}
          {outcomeTone && (
            <span className={`room-pill room-pill-${outcomeTone}`}>
              {outcomeTone === "run" ? <i className="room-pill-dot" /> : <Sym n={pillIcon(outcomeTone)} s={11} />}
              {outcomeLabel}
            </span>
          )}
          <span className={`room-status-pill room-status-cue room-status-${room.status === "Open" ? "open" : "closed"}`}>
            <i className="room-status-dot" /> {room.status}
          </span>
        </div>
        <div className="room-head-meta">
          <span className="room-meta-chip"><Sym n="folder" s={12} cls="room-meta-ic" /> {room.kind}</span>
          <span className="room-meta-sep">·</span>
          <span>{turnCount} turn{turnCount === 1 ? "" : "s"}</span>
          {startedAt && <><span className="room-meta-sep">·</span><span>started {compactAge(startedAt, nowMs)}</span></>}
        </div>
      </header>

      {/* The split: the chat column (scroll + composer) is the whole width until the companion pane is summoned,
          then it shrinks to a left column beside the right-docked pane. `--pane-frac` drives the pane's basis. */}
      <div className="room-split" ref={splitRef} style={{ "--pane-frac": frac } as React.CSSProperties}>
        <div className="room-col">
          <div className="room-scroll">
            <div className="room-thread">
              {room.blocks.map((b) => (
                <TopBlock key={b.id} block={b} anchorId={room.anchorBlockId} nowMs={nowMs} onOpenRun={openRun} onSummonPane={summonPane} onSummonPaneNode={summonPaneNode} />
              ))}
              {turnCount === 0 && <div className="room-empty">No turns yet.</div>}
            </div>
          </div>

          <div className="room-composer">
            <div className="room-composer-inner">
              <LaunchTaskModal inline surface="chat" sessionId={room.sessionId} placeholder="Ask a follow-up — starts a new turn…" onClose={() => {}} onLaunched={openRun} />
            </div>
          </div>
        </div>

        {effPane && binding.open && (
          <>
            <div className="room-divider" onPointerDown={onGripPointerDown} role="separator" aria-orientation="vertical" title="Drag to resize" />
            {/* TODO(D3-reverse): the canvas→journal direction (a node's "view this step in the conversation" scrolls the chat to its journal
                block) is DEFERRED — it would inject a room-scoped affordance into the shared generic WorkflowNode (also
                used by the editor) and thread a nodeId→block-scroll callback down through RunCanvas into the node footer.
                That couples the generic node to the session concern, so it's out of scope here; the forward direction ships. */}
            <RoomRunPane
              runId={effPane.runId}
              turn={effPane.turn}
              view={effPane.view}
              focusNodeId={effPane.node}
              mode={binding.mode}
              onToggleBind={toggleBind}
              jumpToLatest={showJump ? latestTurn?.turnIndex ?? null : null}
              onJumpToLatest={jumpToLatest}
              onViewChange={changePaneView}
              onClose={closePane}
              onOpenRun={openRun}
              onGripPointerDown={onGripPointerDown}
            />
          </>
        )}
      </div>

      {drawer && <RoomDrawer target={drawer} onClose={() => setDrawer(null)} />}
    </section>
    </RoomDrawerContext.Provider>
    </JournalContext.Provider>
  );
}

/** The right-side preview drawer — a panel scoped inside the room, no dimming scrim (the main conversation stays live + full-colour). */
function RoomDrawer({ target, onClose }: { target: DrawerTarget; onClose: () => void }) {
  return (
    <aside className="room-drawer">
      {target.kind === "agent"
        ? <AgentDrawer agent={target.agent} runId={target.runId} onClose={onClose} />
        : target.kind === "modelcall"
          ? <ModelCallDrawer target={target} onClose={onClose} />
          : <FileDrawer target={target} onClose={onClose} />}
    </aside>
  );
}

/** An agent in the drawer — its live terminal, whose Files tab lists the files IT produced (each opens a scoped preview). */
function AgentDrawer({ agent, runId, onClose }: { agent: RoomAgentCard; runId: string; onClose: () => void }) {
  const openDrawer = useRoomDrawer();

  return (
    <>
      <div className="room-drawer-head">
        <span className="room-drawer-ic"><Sym n="cpu" s={15} /></span>
        <span className="room-drawer-title" title={agent.label}>{agent.label}</span>
        <button className="room-drawer-close" onClick={onClose} aria-label="Close"><Sym n="x" s={15} /></button>
      </div>
      <div className="room-drawer-body">
        <div className="room-drawer-term">
          <AgentTerminal agent={toPhaseAgentRef(agent)} onClose={onClose} onOpenFile={(path) => openDrawer({ kind: "file", runId, path, agentRunId: agent.agentRunId })} />
        </div>
      </div>
    </>
  );
}

/** A model call's detail in the drawer — its prompt / result / usage / trace, fetched on demand by the ledger sequence.
 *  A ledger record is immutable, so the fetch never refetches. Offloaded prompt/result are resolved server-side to text. */
function ModelCallDrawer({ target, onClose }: { target: Extract<DrawerTarget, { kind: "modelcall" }>; onClose: () => void }) {
  // RESULT first — the reader wants "what did this call produce", not the prompt wall; the prompt stays one tab away.
  const [tab, setTab] = useState<"result" | "prompt" | "usage" | "trace">("result");
  const q = useQuery({
    queryKey: ["model-call", target.runId, target.sequence],
    queryFn: () => sessionsApi.getModelCallDetail(target.runId, target.sequence),
    staleTime: Infinity,
  });
  const mc = target.call;
  const body = q.data == null ? null : tab === "prompt" ? q.data.prompt : tab === "result" ? q.data.result : tab === "usage" ? q.data.usage : q.data.trace;

  return (
    <>
      <div className="room-drawer-head">
        <span className="room-drawer-ic"><Sym n="sparkle" s={15} /></span>
        <span className="room-drawer-title">{jPurpose(mc.purpose)}{mc.model ? ` · ${mc.model}` : ""}</span>
        <button className="room-drawer-close" onClick={onClose} aria-label="Close"><Sym n="x" s={15} /></button>
      </div>
      <div className="room-mcmetaline">
        {mc.tokens != null && mc.tokens > 0 && <span>{formatTokens(mc.tokens)} tokens</span>}
        {mc.latencyMs != null && <span>{formatLatencyMs(mc.latencyMs)}</span>}
        {mc.costUsd != null && <span>{formatCostUsd(mc.costUsd)}</span>}
        <span className={mc.status === "failed" ? "room-mcmeta-fail" : undefined}>{mc.status}</span>
        {mc.error && <span className="room-mcmeta-fail">{mc.error}</span>}
      </div>
      <div className="room-mctabs">
        {(["result", "prompt", "usage", "trace"] as const).map((t) => (
          <button key={t} className={`room-mctab${tab === t ? " room-mctab-on" : ""}`} onClick={() => setTab(t)}>{t}</button>
        ))}
      </div>
      <div className="room-drawer-body">
        {q.isLoading ? <p className="room-para room-muted">Loading…</p>
          : q.isError ? <p className="room-para room-muted">Couldn't load this model call.</p>
          : q.data == null ? <p className="room-para room-muted">This model call's detail isn't available.</p>
          : body ? <pre className="room-mcpre">{body}</pre>
          : <p className="room-para room-muted">No {tab} recorded for this call.</p>}
      </div>
    </>
  );
}

/** A file preview in the drawer — the header carries a small download icon (only when there's content); the body renders content / diff / notice by kind. */
function FileDrawer({ target, onClose }: { target: Extract<DrawerTarget, { kind: "file" }>; onClose: () => void }) {
  const q = useQuery({
    queryKey: ["roomFile", target.runId, target.path, target.agentRunId ?? ""],
    queryFn: () => sessionsApi.getRoomFile(target.runId, target.path, target.agentRunId),
    staleTime: 60_000,
  });

  const file = q.data;
  const downloadable = !!file && (file.kind === "text" || file.kind === "diff");
  const download = () => file && downloadText(baseName(file.path) + (file.kind === "diff" ? ".diff" : ""), file.text ?? "");

  return (
    <>
      <div className="room-drawer-head">
        <span className="room-drawer-ic"><Sym n="file" s={15} /></span>
        <span className="room-drawer-title" title={target.path}>{baseName(target.path)}</span>
        {downloadable && <button className="room-drawer-act" onClick={download} title="Download file" aria-label="Download file"><Sym n="download" s={14} /></button>}
        <button className="room-drawer-close" onClick={onClose} aria-label="Close"><Sym n="x" s={15} /></button>
      </div>
      <div className="room-drawer-body">
        <FilePreviewBody file={file} loading={q.isLoading} error={q.isError} path={target.path} />
      </div>
    </>
  );
}

/** The file preview body — content / diff / notice by kind. The download affordance lives in the drawer header. */
function FilePreviewBody({ file, loading, error, path }: { file?: RoomFilePreview | null; loading: boolean; error: boolean; path: string }) {
  if (loading) return <div className="room-fprev"><div className="room-fprev-empty"><span className="room-fprev-spin" /> <p className="room-fprev-note">Loading preview…</p></div></div>;

  if (!file) return <FilePreviewNotice icon="alert" title={baseName(path)} note={error ? "Couldn't load this file." : "This file isn't part of the turn's change set."} />;

  if (file.kind === "binary" || file.kind === "unavailable")
    return <FilePreviewNotice icon={file.kind === "binary" ? "file" : "alert"} title={baseName(file.path)} note={file.note ?? "Preview isn't available for this file."} sourceUrl={file.sourceUrl} />;

  return (
    <div className="room-fprev">
      <div className="room-fprev-meta">
        {file.changeKind && <span className="room-fprev-tag">{file.changeKind}</span>}
        <span className="room-fprev-path">{file.path}</span>
        {file.truncated && <span className="room-fprev-trunc">truncated</span>}
      </div>
      <pre className={`room-fprev-body room-fprev-${file.kind}`}>{renderBody(file)}</pre>
      {file.sourceUrl && <div className="room-fprev-foot"><a className="room-btn" href={file.sourceUrl} target="_blank" rel="noreferrer"><Sym n="pr" s={13} /> Open in PR</a></div>}
    </div>
  );
}

/** A file body: a diff colourises its +/- lines; plain text renders verbatim. */
function renderBody(file: RoomFilePreview): ReactNode {
  const text = file.text ?? "";
  if (file.kind !== "diff") return text;

  return text.split("\n").map((line, i) => {
    const tone = line.startsWith("+") && !line.startsWith("+++") ? "add" : line.startsWith("-") && !line.startsWith("---") ? "del" : line.startsWith("@@") ? "hunk" : "ctx";
    return <span key={i} className={`room-diffline room-diffline-${tone}`}>{line || " "}{"\n"}</span>;
  });
}

/** The degraded / notice state of the file drawer — a centered message with an optional PR link. */
function FilePreviewNotice({ icon, title, note, sourceUrl }: { icon: SymName; title: string; note: string; sourceUrl?: string | null }) {
  return (
    <div className="room-fprev">
      <div className="room-fprev-empty">
        <Sym n={icon} s={26} />
        <div className="room-fprev-name">{title}</div>
        <p className="room-fprev-note">{note}</p>
      </div>
      {sourceUrl && <div className="room-fprev-foot"><a className="room-btn" href={sourceUrl} target="_blank" rel="noreferrer"><Sym n="pr" s={13} /> Open in PR</a></div>}
    </div>
  );
}

/** Trigger a client-side download of in-memory text (the drawer serves the preview body, no extra round-trip). */
function downloadText(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

/** The last path segment — the drawer title + download filename. */
function baseName(path: string): string {
  const i = path.lastIndexOf("/");
  return i >= 0 ? path.slice(i + 1) : path;
}

/** A top-level block: the user's message bubble, an assistant turn, or a forward-compat fallback. */
function TopBlock({ block, anchorId, nowMs, onOpenRun, onSummonPane, onSummonPaneNode }: { block: RoomBlock; anchorId?: string | null; nowMs: number; onOpenRun: (runId: string) => void; onSummonPane?: (turn: number, view: PaneView) => void; onSummonPaneNode?: (turn: number, node: string) => void }) {
  if (block.type === "user_message") return <UserBubble text={block.text} at={block.at} nowMs={nowMs} />;
  if (block.type === "assistant_turn") return <AssistantTurn turn={block} anchored={anchorId === block.id} nowMs={nowMs} onOpenRun={onOpenRun} onSummonPane={onSummonPane} onSummonPaneNode={onSummonPaneNode} />;
  return <p className="room-para room-muted">{describeUnknown(block)}</p>;
}

/** The user's message — a right-aligned bubble with a "You · time" label, clamped with a "Show full prompt" toggle. */
function UserBubble({ text, at, nowMs }: { text: string; at?: string | null; nowMs: number }) {
  const long = text.length > 280;
  const [open, setOpen] = useState(false);

  return (
    <div className="room-user-wrap">
      <div className="room-user-by">You{at ? ` · ${compactAge(at, nowMs)}` : ""}</div>
      <div className="room-user">
        <div className="room-user-clamp" data-clamped={long && !open || undefined}>
          <p><Inline text={text} /></p>
          {long && !open && <div className="room-user-fade" />}
        </div>
        {long && (
          <button className="room-link" onClick={() => setOpen((o) => !o)}>{open ? "Show less" : "Show full prompt"}</button>
        )}
      </div>
    </div>
  );
}

/** The AI's reply for one turn — header (avatar + name + status + duration + collapse), then the transcript body. */
function AssistantTurn({ turn, anchored, nowMs, onOpenRun, onSummonPane, onSummonPaneNode }: { turn: AssistantTurnBlock; anchored: boolean; nowMs: number; onOpenRun: (runId: string) => void; onSummonPane?: (turn: number, view: PaneView) => void; onSummonPaneNode?: (turn: number, node: string) => void }) {
  const live = isRunActive(turn.status);

  // D3 forward jump: a per-turn node-focus callback the journal beats consume via PaneNodeFocusContext. Bound to THIS
  // turn's index; null when the turn has no runId (no canvas to open), so a beat then renders no "在Canvas查看" affordance.
  const focusPaneNode = onSummonPaneNode && turn.runId ? (nodeId: string) => onSummonPaneNode(turn.turnIndex, nodeId) : null;

  // The companion-pane summons for THIS turn — the run detail used to open in a modal (removed in D4); now the footer
  // "開啟Canvas" pins the canvas mini-tab and a failure diagnostic's "View trace" pins the trace mini-tab. Undefined when the
  // turn has no run to bind a pane to (or no summon callback is wired — a standalone render).
  const openCanvas = onSummonPane && turn.runId ? () => onSummonPane(turn.turnIndex, "canvas") : undefined;
  const openTrace = onSummonPane && turn.runId ? () => onSummonPane(turn.turnIndex, "trace") : undefined;
  // TODO(D2-followup): auto-collapse non-bound turns to EXECUTION + a one-line summary when the pane is bound to another
  // turn — deferred from D2 (it's a larger AssistantTurn rendering change; D2 ships the binding + toggle + jump chip).
  const [open, setOpen] = useState(anchored || live);

  // Opening a specific run (from the runs list or a deep link) resolves to its turn as the room's anchor. Scroll that
  // turn into view once on mount so the click lands ON that turn, not at the top of a long multi-turn conversation.
  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (anchored) rootRef.current?.scrollIntoView?.({ block: "start", behavior: "auto" });
  }, [anchored]);

  const tone = statusTone(turn.status, live);

  const hasDecision = turn.blocks.some((b) => b.type === "decision");
  const decisions = usePendingDecisions(hasDecision && live);
  const agentIds = new Set(turn.blocks.flatMap((b) => (b.type === "agent_group" ? b.agents.map((a) => a.agentRunId) : [])));
  const liveDecisions = decisions.data ? decisionsForRun(decisions.data, turn.runId, agentIds) : [];
  const pdById = new Map(liveDecisions.map((d) => [d.id, d]));

  // Journal mode: this turn's chronological steps replace the Room's narrative blocks (agent groups / supervisor steps /
  // the live ticker) while the frame — execution map, plan checklist, result card — is reused as-is. Only the FOCUSED
  // turn is walked into steps (others are collapsed with none), so a stepless turn keeps the Room narrative. Null in Room mode.
  const journal = useJournal();
  const journalTurn = journal?.turns.find((t) => t.turnIndex === turn.turnIndex && t.steps.length > 0) ?? null;

  // The outcome belongs at the END — the green RESULT card (success) or the red diagnostic (failure), never echoed at
  // the top. When the turn carries either, drop the opening lead so the top is just the execution map + the flow, and
  // the outcome reads once, at the bottom. A still-running turn has neither, so its lead surfaces the live one-liner.
  const hasOutcome = turn.blocks.some((b) => b.type === "final_answer" || b.type === "diagnostic");
  const lead = turn.summary && !GENERIC_SUMMARY.has(turn.summary) && !hasOutcome ? turn.summary : null;

  // Failure-first ordering: lift the diagnostic (humanized cause + typed remediation) out of the bottom result stack so
  // it renders directly under the execution rail — WHY it failed and how to recover, before the narrative. `bodyBlocks`
  // is the rest, in original order; a success / running turn has no diagnostic, so nothing moves.
  const { hoisted: failureCard, rest: bodyBlocks } = partitionForFailureHoist(turn.blocks);

  return (
    <RunActionsContext.Provider value={{ runId: turn.runId, isTerminal: !live }}>
      <RunOpenContext.Provider value={onOpenRun}>
        <PaneNodeFocusContext.Provider value={focusPaneNode}>
        <div className="room-turn" ref={rootRef}>
          <div className="room-turn-head">
            <span className="room-av"><Sym n="terminal" s={13} /></span>
            <span className="room-av-name">CodeSpace</span>
            <span className={`room-pill room-pill-${tone}${open && live ? " room-pill-steady" : ""}`}>
              {tone === "run" ? <i className="room-pill-dot" /> : <Sym n={pillIcon(tone)} s={11} />}
              {pillLabel(turn.status, live)}
            </span>
            <span className="room-turn-meta">Turn {turn.turnIndex}{turnMeta(turn, nowMs, live)}</span>
            <TurnAttempts attempts={turn.attempts ?? []} nowMs={nowMs} onOpenRun={onOpenRun} />
            <button className="room-collapse" title={open ? "Collapse turn" : "Expand turn"} onClick={() => setOpen((o) => !o)}>
              <Sym n={open ? "chevron-down" : "chevron-right"} s={14} />
            </button>
          </div>

          {open && (
            <div className="room-turn-body">
              {lead && <p className="room-lead"><Inline text={lead} /></p>}

              {turn.map && turn.map.steps.length > 0 && <RoomExecution steps={turn.map.steps} turnStatus={turn.status} onSummon={openCanvas} />}

              {failureCard && <InnerBlock key={failureCard.id} block={failureCard} pdById={pdById} onOpenTrace={openTrace} />}

              {journalTurn ? (() => {
                // Curated journal frame: TOP keeps only the primary plan checklist ② (execution map ① is already above).
                // The middle is the decision skeleton ③, then the live "working…" ticker while the turn runs, then the
                // result ⑥. Supporting stat rows (Files changed / Tools / Reasoning) drop to a collapsed strip AFTER the
                // result so they never interrupt the story. Any block the journal doesn't deliberately replace — an
                // unknown/future additive type — still falls through InnerBlock so it degrades to a faint line, never vanishes.
                // When the journal has a PLAN decision step, the plan renders INLINE under it (the causal spine plan →
                // dispatch → agents), so nothing is pinned at the top and the redundant "Plan · N subtasks" stat is dropped.
                // A run with NO plan step (e.g. a flow.map run whose plan comes from a planner node, or a legacy block-only
                // run) still pins its plan block at the top so the plan never vanishes.
                // Same predicate as planStepIds (below) so the top-pin drop and the inline render stay in lockstep —
                // a plan-bearing step the inline path skips (a hidden stop) never leaves the plan neither pinned nor inline.
                const hasInlinePlan = journalTurn.steps.some((s) => s.plan.length > 0 && !isStopDecision(s));
                // The rich plan checklist card (per-item status / artifacts / details) rendered INLINE under the plan beat
                // — the same component the room shows, not a re-implemented plain list.
                const planCard = turn.blocks.find((b): b is PlanChecklistBlock => b.type === "plan_checklist");
                const topPlan = hasInlinePlan ? undefined : (planCard ?? turn.blocks.find((b) => b.type === "stat" && b.kind === "subtasks"));
                const resultBlocks = bodyBlocks.filter((b) => JOURNAL_RESULT.has(b.type));
                // A plain run with NO agent-carrying beat (a single agent, or a linear agent chain — no supervisor
                // spawn, no map dispatch) has no beat to hang its agent cards on, so the ③ skeleton would show only
                // folded lifecycle and the agent's work would vanish. Render the room's own agent_group block(s) in
                // that case — the SAME cards the room shows. The predicate is "some beat CARRIES agents", not "some
                // beat exists": a REVIEW beat (in-flight or landed) carries no cards, so it must not suppress them —
                // otherwise a plain run's cards vanish for the whole review window. Card-carrying beats keep this
                // empty, so cards are never doubled.
                const beatsCarryAgents = journalTurn.steps.some((s) => s.beat && s.agents.length > 0);
                const agentGroupBlocks = beatsCarryAgents ? [] : turn.blocks.filter((b) => b.type === "agent_group");
                // Supporting rows AFTER the result — Files changed / Tools / Reasoning. Drop the "Plan · N subtasks" stat
                // when the plan is shown inline (redundant); never re-render the one block pinned at the top.
                const statBlocks = turn.blocks.filter((b) => b.type === "stat" && b.id !== topPlan?.id && !(hasInlinePlan && b.kind === "subtasks"));
                const passThrough = turn.blocks.filter((b) => !JOURNAL_HANDLED.has(b.type));
                // The turn's agent cards by run id — so a plan item opens its agent's terminal with the SAME full metrics
                // an agent-card row would (model · tokens · files · tools · duration · cost), not a bare id+title stub.
                const agentCards = new Map(journalTurn.steps.flatMap((s) => s.agents).map((c) => [c.agentRunId, journalToRoomCard(c)] as const));
                return (
                  <PlanAgentCardsContext.Provider value={agentCards}>
                    {topPlan && <InnerBlock key={topPlan.id} block={topPlan} pdById={pdById} onOpenTrace={openTrace} />}
                    <JournalNarrative turn={journalTurn} planCard={hasInlinePlan ? planCard : undefined} />
                    {agentGroupBlocks.map((b) => <InnerBlock key={b.id} block={b} pdById={pdById} onOpenTrace={openTrace} />)}
                    {resultBlocks.map((b) => <InnerBlock key={b.id} block={b} pdById={pdById} onOpenTrace={openTrace} />)}
                    {statBlocks.length > 0 && (
                      <div className="room-jsupport">
                        {statBlocks.map((b) => <InnerBlock key={b.id} block={b} pdById={pdById} onOpenTrace={openTrace} />)}
                      </div>
                    )}
                    {passThrough.map((b) => <InnerBlock key={b.id} block={b} pdById={pdById} onOpenTrace={openTrace} />)}
                  </PlanAgentCardsContext.Provider>
                );
              })() : (
                // The live ticker is suppressed here — its "working…" line + the run's Stop button are unified into the
                // pinned LiveRunBar below, so the running signal + the Stop action live together in ONE place.
                bodyBlocks.filter((b) => b.type !== "live_activity").map((b) => <InnerBlock key={b.id} block={b} pdById={pdById} onOpenTrace={openTrace} />)
              )}

              {live && <TurnLiveStream runId={turn.runId} />}

              <TurnActions actions={turn.actions} turn={turn} onOpenCanvas={openCanvas} onOpenRun={onOpenRun} />

              {live && <LiveRunBar turn={turn} />}
            </div>
          )}
        </div>
        </PaneNodeFocusContext.Provider>
      </RunOpenContext.Provider>
    </RunActionsContext.Provider>
  );
}

/**
 * The turn's rerun/replay attempt switcher — a compact "attempt N of M ▾" chip in the turn header (shown only when the
 * turn was rerun > 1 time). Opening it drops a menu of every attempt (status · when); picking one navigates to THAT
 * attempt's run, so the whole turn re-renders on the selected attempt's flow (the backend focuses the anchor run). The
 * shown attempt reads "shown". Attempts are a TURN property — the whole turn reran — so this lives on the turn header,
 * not one agent card (a per-agent rerun switches inside the agent's terminal instead).
 */
function TurnAttempts({ attempts, nowMs, onOpenRun }: { attempts: RoomTurnAttempt[]; nowMs: number; onOpenRun: (runId: string) => void }) {
  const [open, setOpen] = useState(false);

  if (attempts.length < 2) return null;

  const current = attempts.find((a) => a.isCurrent) ?? attempts[attempts.length - 1];

  return (
    <div className="room-attempts-hd">
      <button className="room-attempts-chip" onClick={() => setOpen((o) => !o)} aria-expanded={open} title="Switch attempt">
        attempt {current.attemptNumber} of {attempts.length}
        <Sym n="chevron-down" s={11} />
      </button>

      {open && (
        <>
          <div className="room-attempts-mask" onClick={() => setOpen(false)} />
          <div className="room-attempts-menu" role="menu">
            {attempts.map((a) => (
              <button
                key={a.runId}
                className={`room-attempts-item room-attempt-${statusTone(a.status, false)}`}
                data-current={a.isCurrent || undefined}
                role="menuitem"
                onClick={() => { setOpen(false); if (!a.isCurrent) onOpenRun(a.runId); }}
              >
                <span className="room-attempt-dot" />
                <span className="room-attempt-n">attempt {a.attemptNumber}</span>
                <span className="room-attempt-status">{pillLabel(a.status, false)}</span>
                <span className="room-attempt-when">· {compactAge(a.at, nowMs)}</span>
                {a.isCurrent && <span className="room-attempt-shown">shown</span>}
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

/** The execution map — the design's bordered EXECUTION panel: backend-ordered stages as status circles with a label
 *  + per-stage detail, joined by connectors that read solid / dashed / animated by the surrounding stage states. */
function RoomExecution({ steps, turnStatus, onSummon }: { steps: ExecutionMapStep[]; turnStatus: WorkflowRunStatus; onSummon?: () => void }) {
  // A halted run's later stages never ran — so an idle stage reads "not reached", not the backend's "stopped" (which
  // implies it was doing something). The caption names why the tail is empty; "stopped" now means only a user cancel.
  const failed = turnStatus === "Failure";
  const halted = failed || turnStatus === "Cancelled";

  return (
    <div className="room-exec">
      <div className="room-exec-head">
        <div className="room-exec-label">Execution</div>
        {/* Summon the run companion pane — opens this turn's RunCanvas docked on the right (Claude-artifacts style). */}
        {onSummon && <button type="button" className="room-exec-canvas" onClick={onSummon} title="Open this turn's execution graph in the canvas"><span className="room-exec-canvas-ic" aria-hidden="true">⧉</span> Open canvas</button>}
      </div>
      <div className="room-exec-flow">
        {steps.map((s, i) => {
          const idle = s.status === "Queued" || s.status === "Skipped" || s.status === "Pending";
          const deliver = s.label === "Deliver" && s.status === "Done";
          const detail = halted && idle ? "not reached" : s.detail;
          return (
            <Fragment key={s.id}>
              {i > 0 && <span className={`room-exec-conn room-exec-conn-${connKind(steps[i - 1].status, s.status)}`} aria-hidden="true" />}
              <div className="room-exec-node" data-idle={idle || undefined}>
                <span className={`room-exec-dot room-exec-dot-${execDotClass(s.status)}`} aria-hidden="true"><Sym n={execDotIcon(s.status, deliver)} s={s.status === "Running" ? 13 : idle ? 11 : 13} cls={s.status === "Running" ? "room-spin" : undefined} /></span>
                <div className="room-exec-text">
                  <span className="room-exec-name">{s.label}</span>
                  {detail && <span className={`room-exec-detail room-exec-detail-${execDotClass(s.status)}`}>{detail}</span>}
                </div>
              </div>
            </Fragment>
          );
        })}
      </div>
      {halted && <p className="room-exec-note">{failed ? "A failed step stopped the run — the steps after it didn't run." : "Stopped — the remaining steps didn't run."}</p>}
    </div>
  );
}

// ─── Journal mode: the chronological ③ timeline that replaces the Room's narrative blocks ───

/** Result-family blocks rendered BELOW the journal timeline — the final answer / delivery / diagnostic ⑥ + any live pending decision. The supervisor "stop" is internal lifecycle, so its outcome lives HERE (the result card), never as a step in ③. */
const JOURNAL_RESULT = new Set<RoomBlock["type"]>(["delivery", "final_answer", "diagnostic", "decision"]);

/** Every KNOWN inner-block type the journal deliberately consumes: execution map ① (via turn.map), plan ②, the decision skeleton ③ in place of agent_group, the live ticker, the result ⑥, and the stat support strip. Anything NOT here is an unknown/future additive type and falls through InnerBlock so it degrades to a faint line rather than vanishing (matching the room's forward-compat contract). (The room's narrative_step stack was deleted in P6 — the journal ③ owns that surface.) */
const JOURNAL_HANDLED = new Set<RoomBlock["type"]>(["execution_map", "agent_group", "plan_checklist", "stat", "live_activity", ...JOURNAL_RESULT]);

/** The journal's ③ shows only the supervisor's decision skeleton (planned · dispatched · asked · merged). Everything else — thinking, tool calls, model calls, agent-file events, run/node lifecycle — folds into the "background steps" disclosure; the raw text lives in the trace drawer, never flat on the main transcript. */
function isBackgroundStep(s: JournalStep): boolean {
  return !s.beat;
}

/** The supervisor "stopped" decision is internal lifecycle — its outcome is the result card ⑥, so it never takes a step
 *  in ③. The VERB is the authority when present (a REVISE beat's free-text reason may legitimately contain "stopped" —
 *  it must never be swallowed); the title regex remains only as the fallback for a verbless beat. */
function isStopDecision(s: JournalStep): boolean {
  if (!s.beat) return false;
  if (s.verb) return jVerbKey(s.verb) === "stop";
  return /\bstopped\b/i.test(s.title);
}

/** Strip the internal-actor "Supervisor " voice so a decision reads as a plain past-tense beat ("Planned the work", "Dispatched 4 agents", "Asked you", "Merged the results"). */
function jTitle(raw: string): string {
  const t = raw.replace(/^Supervisor[:\s]+/i, "").replace(/^spawned\b/i, "Dispatched");
  return t.charAt(0).toUpperCase() + t.slice(1);
}

/** One journal agent card → the Room's RoomAgentCard shape, so it renders with the SAME agent-card look + the SAME
 *  row-click → side-drawer (terminal / files / trace) as the room, and its full metrics (model · tokens · files · tools ·
 *  duration · cost) carry into any drawer opened for it. The readable subtask title rides as summary (the row's hover) +
 *  assignedSubtask (the drawer's allocation strip). */
function journalToRoomCard(c: JournalAgentCard): RoomAgentCard {
  return {
    agentRunId: c.agentRunId,
    label: c.label,
    summary: c.assignedSubtask ?? null,
    assignedSubtask: c.assignedSubtask ?? null,
    status: c.status,
    error: c.error ?? null,
    model: c.model ?? null,
    harness: c.harness ?? null,
    tokens: c.tokens ?? null,
    costUsd: c.costUsd ?? null,
    filesChanged: c.filesChanged ?? null,
    changedFiles: c.files.map((f) => f.path),
    toolCount: c.toolCount ?? null,
    durationMs: c.durationMs ?? null,
    resumed: c.resumed,
    review: c.review ?? null,
  };
}

/** Adapt a spawn/retry decision's journal agent cards into the Room's AgentGroupBlock so the wave renders with the SAME agent-card look and the SAME row-click → side-drawer as the room. */
function journalAgentsGroup(step: JournalStep): AgentGroupBlock {
  return { type: "agent_group", id: `jrnl-${step.id}-agents`, seq: 0, title: "Agents", agents: step.agents.map(journalToRoomCard) };
}

/** A turn's journal agent cards keyed by agent-run id — so a plan checklist item (which knows only its agent's id) can
 *  open that agent's terminal with the SAME full metrics an agent-card row would, instead of a bare id+title stub.
 *  Empty in Room mode (no journal), where the plan card falls back to the minimal stub. */
const PlanAgentCardsContext = createContext<Map<string, RoomAgentCard>>(new Map());

/** The chronological work timeline for one turn — the journal's ③, in the Room's mono style. Consecutive background steps collapse into one in-place disclosure so the story reads clean but nothing is lost. */
function JournalNarrative({ turn, planCard }: { turn: JournalTurn; planCard?: PlanChecklistBlock }) {
  const rows: ({ kind: "step"; step: JournalStep } | { kind: "fold"; steps: JournalStep[] })[] = [];
  for (const step of turn.steps) {
    if (isStopDecision(step)) continue; // the "stopped" outcome lives in the result card ⑥, not as a step

    const last = rows[rows.length - 1];
    if (isBackgroundStep(step)) {
      if (last && last.kind === "fold") last.steps.push(step);
      else rows.push({ kind: "fold", steps: [step] });
    } else {
      rows.push({ kind: "step", step });
    }
  }

  // One lightweight actor lane names WHO is driving the turn, so every beat below reads as that actor's without
  // repeating the name on each line. A supervisor run (its beats carry supervisor decision verbs) reads "Supervisor";
  // a plain workflow / flow.map run (only node-dispatch beats) reads "Workflow" — the lane stays generic across shapes.
  const hasDecision = turn.steps.some((s) => s.beat && !isStopDecision(s));
  const isSupervised = turn.steps.some((s) => s.beat && SUPERVISOR_VERBS.has(s.verb ?? ""));

  // Each plan version renders its OWN card at its OWN beat, so a re-plan reads straight down the timeline —
  // plan(v1) → asked → plan(v2) → asked — instead of the newest card jumping onto the oldest beat. The CURRENT version
  // (the LATEST plan beat) gets the rich room card; it decides interactive-vs-read-only itself (buttons only while it's
  // still awaiting confirmation on a live run). Every EARLIER version renders READ-ONLY from its own authored subtasks —
  // it was superseded before it ran, so the bare titles ARE its truthful record, and its request-changes outcome shows
  // on the ask beat right below it. The version number is the beat's 1-based rank among the turn's plan beats.
  const planStepIds = turn.steps.filter((s) => s.plan.length > 0 && !isStopDecision(s)).map((s) => s.id);
  const currentPlanStepId = planStepIds[planStepIds.length - 1];
  const planVersionById = new Map(planStepIds.map((id, i) => [id, i + 1]));

  // Only the NEWEST pending ask gets the inline answer bar — the answer endpoint targets the newest unanswered ask,
  // so a bar under an older (degraded/superseded) question would silently answer the wrong one. Plan-confirmation
  // parks are excluded: the plan checklist card is that park's structured answer surface.
  const pendingAskIds = turn.steps.filter((s) => s.beat && jVerbKey(s.verb) === "ask" && !s.answer && !s.planConfirmation).map((s) => s.id);
  const answerableAskId = pendingAskIds[pendingAskIds.length - 1];

  return (
    <>
      {hasDecision && (
        <div className="room-jactor">
          <span className="room-jactor-dot" />
          <span className="room-jactor-name">CodeSpace</span>
          <span className="room-jactor-sub">{isSupervised ? "· planning this turn" : "· running this turn"}</span>
        </div>
      )}
      <div className="room-jrnl">
        {rows.map((r, i) => (r.kind === "step"
          ? <JournalStepRow key={r.step.id} step={r.step} planCard={r.step.id === currentPlanStepId ? planCard : undefined} planVersion={planVersionById.get(r.step.id)} planSuperseded={r.step.id !== currentPlanStepId} askAnswerable={r.step.id === answerableAskId} />
          : partitionBackground(r.steps).map((g, j) => <JournalFold key={`fold-${i}-${g.category}-${j}`} steps={g.steps} category={g.category} />)))}
        {turn.steps.length === 0 && <p className="room-para room-muted">No steps recorded yet.</p>}
      </div>
    </>
  );
}

function JournalFold({ steps, category }: { steps: JournalStep[]; category: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="room-jfold">
      <button onClick={() => setOpen((v) => !v)} aria-expanded={open}>{open ? "▾" : "▸"} {foldLabel(category, steps)}</button>
      {open && <div className="room-jfold-steps">{steps.map((s) => category === "model"
        ? <ModelCallRow key={s.id} step={s} />
        : <JournalStepRow key={s.id} step={s} muted />)}</div>}
    </div>
  );
}

/** One model call in the expanded model fold — a legible row (purpose · model · tokens · latency · cost · status) off the
 *  step's structured facts. A model call is the cost + intelligence source of an AI workflow, so once the fold is opened
 *  the reader sees WHAT decided WHAT, at what token cost + latency + spend — not a bare "Model call" line. Falls back to
 *  the muted step row if the facts didn't attach (a pre-enrichment step). */
function ModelCallRow({ step }: { step: JournalStep }) {
  const mc = step.modelCall;
  const openDrawer = useRoomDrawer();
  const run = useContext(RunActionsContext);
  if (!mc) return <JournalStepRow step={step} muted />;
  const failed = mc.status === "failed";
  // A completed-but-cut-off call (length cap / content filter) — surfaced as an amber caution, distinct from a hard failure.
  const cutOff = !failed && !!mc.finishNote;
  const sequence = modelCallSequence(step.id);
  const clickable = run != null && sequence != null;
  return (
    <button className={`room-mcrow${failed ? " room-mcrow-err" : ""}`} disabled={!clickable}
      onClick={() => clickable && openDrawer({ kind: "modelcall", runId: run!.runId, sequence: sequence!, call: mc })}>
      <span className="room-mctime">{jTime(step.at)}</span>
      <span className="room-mcpurpose">{jPurpose(mc.purpose)}</span>
      <span className="room-mcmeta">
        {mc.model && <span className="room-mcitem room-mcmodel" title={`Model · ${mc.model}`}><Sym n="sparkle" s={10} cls="room-mcic" /> {mc.model}</span>}
        {mc.tokens != null && mc.tokens > 0 && <span className="room-mcitem" title={`${mc.tokens.toLocaleString()} tokens`}><Sym n="cpu" s={10} cls="room-mcic" /> {formatTokens(mc.tokens)} tokens</span>}
        {mc.latencyMs != null && <span className="room-mcitem" title="Latency"><Sym n="clock" s={10} cls="room-mcic" /> {formatLatencyMs(mc.latencyMs)}</span>}
        {mc.costUsd != null && <span className="room-mcitem room-mccost" title="Estimated cost">{formatCostUsd(mc.costUsd)}</span>}
        {failed && mc.error && <span className="room-mcitem room-mcerr" title={mc.error}><Sym n="alert" s={10} cls="room-mcic" /> {mc.error.length > 72 ? mc.error.slice(0, 71) + "…" : mc.error}</span>}
        {cutOff && <span className="room-mcitem room-mcwarn" title={`The answer was cut off — ${mc.finishNote}`}><Sym n="alert" s={10} cls="room-mcic" /> {mc.finishNote}</span>}
      </span>
      <span className={`room-mcstatus${failed ? " room-mcstatus-err" : cutOff ? " room-mcstatus-warn" : ""}`}>{failed ? "failed" : cutOff ? "cut off" : "done"}</span>
      {clickable && <Sym n="chevron-right" s={11} cls="room-mcchev" />}
    </button>
  );
}

/** A model-call step's id is "record-{sequence}" (the ledger sequence) — the key the drawer fetches its detail by. */
function modelCallSequence(stepId: string): number | null {
  const m = /^record-(\d+)$/.exec(stepId);
  return m ? parseInt(m[1], 10) : null;
}

/** The friendly word for a model call's purpose (its interaction kind) — the common ones read naturally, an unknown kind shows verbatim. */
function jPurpose(kind: string): string {
  switch (kind) {
    case "supervisor.decision": return "decision";
    case "supervisor.revise": return "revision";
    case "critic.review": return "critic review";
    case "agent.critic": return "output critic";
    case "plan.author": return "planner";
    case "plan.confirm": return "plan review";
    case "llm.complete": return "synthesis";
    default: return kind;
  }
}

/** The INTENT fragment for a model fold's label — the calls' distinct purposes so the collapsed line already says what
 *  the model was doing ("decision" / "revision" / "critic review"), not just that it was called. Up to two named, the
 *  rest counted; empty when no call carries a purpose fact yet (a pre-enrichment poll) so the label degrades cleanly. */
function foldIntent(steps: JournalStep[]): string {
  const labels = [...new Set(steps.map((s) => s.modelCall?.purpose).filter((p): p is string => !!p && p !== "model call").map(jPurpose))];
  if (labels.length === 0) return "";
  return ` · ${labels.slice(0, 2).join(" · ")}${labels.length > 2 ? ` +${labels.length - 2}` : ""}`;
}

/** A per-call USD cost — a few cents needs 3–4 decimals to read, a larger spend 2. A real-but-tiny spend that would round
 *  to $0.0000 shows a floor sentinel so it never reads as free. Never scientific notation. */
function formatCostUsd(usd: number): string {
  if (usd === 0) return "$0";
  if (usd > 0 && usd < 0.00005) return "<$0.0001";
  return usd < 0.01 ? `$${usd.toFixed(4)}` : usd < 1 ? `$${usd.toFixed(3)}` : `$${usd.toFixed(2)}`;
}

/** Per-call latency reads on a millisecond scale (most calls are sub-second) — so show "NNNms" below a second and "N.Ns"
 *  up to ~10s, falling back to the coarse whole-second/minute formatter for a long call. Reusing formatDurationMs alone
 *  would floor every fast call to "0s". */
function formatLatencyMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 10000) return `${(ms / 1000).toFixed(1)}s`;
  return formatDurationMs(ms);
}

/** The display category of a folded background step — so the fold reads "3 model calls · 25.6k tokens" not a flat "N
 *  background steps" that hides cost + intelligence. Model calls / tool calls / reasoning each get their own class; the
 *  rest (lifecycle housekeeping, and any other non-beat kind) stays generic. */
function stepCategory(kind: string): "model" | "tool" | "reasoning" | "review" | "background" {
  switch (kind) {
    case "model_call": return "model";
    case "tool": return "tool";
    case "thinking": return "reasoning";
    case "review": return "review";
    default: return "background";   // lifecycle housekeeping + any other non-beat kind fold into plain "background steps"
  }
}

/** A consecutive run of folded background steps, split into typed groups by category (first-appearance order preserved),
 *  so model calls / tool calls / reasoning each collapse into their OWN labeled fold instead of one opaque
 *  "N background steps". */
function partitionBackground(steps: JournalStep[]): { category: string; steps: JournalStep[] }[] {
  const order: string[] = [];
  const byCategory = new Map<string, JournalStep[]>();
  for (const s of steps) {
    const cat = stepCategory(s.kind);
    if (!byCategory.has(cat)) { byCategory.set(cat, []); order.push(cat); }
    byCategory.get(cat)!.push(s);
  }
  return order.map((cat) => ({ category: cat, steps: byCategory.get(cat)! }));
}

/** A model call's total tokens, parsed from its "kind · model · N tokens" detail (RunRecordTimelineMap.ModelCallSummary).
 *  0 when the call reported no usage — so the fold's token sum omits it rather than counting a phantom zero. */
function stepTokens(step: JournalStep): number {
  const m = /·\s*([\d,]+)\s*tokens/.exec(step.detail ?? "");
  return m ? parseInt(m[1].replace(/,/g, ""), 10) : 0;
}

/** The fold's label by category — model calls surface their summed token cost (the thing that must be SEEN, not buried);
 *  the others just count. */
function foldLabel(category: string, steps: JournalStep[]): string {
  const n = steps.length;
  const plural = n === 1 ? "" : "s";
  switch (category) {
    case "model": {
      const tokens = steps.reduce((sum, s) => sum + stepTokens(s), 0);
      return `${n} model call${plural}${foldIntent(steps)}${tokens > 0 ? ` · ${formatTokens(tokens)} tokens` : ""}`;
    }
    case "tool": return `${n} tool call${plural}`;
    case "reasoning": return `${n} reasoning step${plural}`;
    case "review": return `${n} reviewer step${plural}`;
    default: return `${n} background step${plural}`;
  }
}

function JournalStepRow({ step, muted, planCard, planVersion, planSuperseded, askAnswerable }: { step: JournalStep; muted?: boolean; planCard?: PlanChecklistBlock; planVersion?: number; planSuperseded?: boolean; askAnswerable?: boolean }) {
  // Raw thinking never renders flat on the main transcript — it lives in the trace drawer. A folded thinking step,
  // even when its disclosure is opened, shows only its one-line title; the full chain-of-thought stays in the trace.
  const showDetail = step.detail && step.kind !== "thinking";
  // An ASK beat carries the operator's answer as a STRUCTURED field (step.answer). The shared detail still folds it onto
  // the question as "{question} — {answer}", so use the known answer to locate + strip that suffix — precise, unlike an
  // em-dash split which mis-fires when the question itself contains a dash. The answer then reads as its OWN "└ answer ·"
  // line (like the "└ why" rationale line). Scoped to ASK beats with an answer; a still-pending ask shows just the question.
  const isAsk = step.beat && jVerbKey(step.verb) === "ask";
  const ask = isAsk && step.answer ? askParts(step.detail, step.answer) : null;
  // D3 forward jump — render the "在Canvas查看" chip only when a pane-focus callback is in context (an interactive room with
  // a bound turn) AND this step maps to a real canvas node. A folded/muted background row keeps the timeline clean (no chip).
  const focusNode = usePaneNodeFocus();
  const jumpNodeId = journalStepNodeId(step);
  return (
    <div className={`room-jstep room-jtone-${jTone(step.tone)}${step.milestone ? " room-jkey" : ""}${muted ? " room-jmuted" : ""}`}>
      <span className="room-jnode" />
      <div className="room-jline">
        <span className="room-jtime">{jTime(step.at)}</span>
        {step.beat
          ? <span className={`room-jpill room-jpill-${jVerbKey(step.verb)}`}>{jVerbKey(step.verb)}</span>
          : <span className={`room-jkind room-jkind-${step.kind}`}>{jKindLabel(step.kind)}</span>}
        {step.reviewEscalation && <span className="room-jpill room-jpill-blocked">review-blocked</span>}
        <span className="room-jtitle">{step.beat ? jTitle(step.title) : step.title}</span>
        {focusNode && jumpNodeId && !muted && (
          <button type="button" className="room-jjump" title="Open and focus this node in the canvas" onClick={() => focusNode(jumpNodeId)}>⧉ View in canvas</button>
        )}
      </div>
      {step.rationale && <div className="room-jwhy"><span className="room-jwhy-l">└ why · </span>{step.rationale}</div>}
      {step.draft && <div className="room-jwhy room-jdraftline"><span className="room-jwhy-l">└ replaced a draft · </span>{step.draft}</div>}
      {step.review && <ReviewVerdictCard review={step.review} />}
      {showDetail && (ask
        ? <>
            {ask.question && <JournalAskQuestion text={ask.question} tone={jTone(step.tone)} />}
            <div className="room-janswer"><span className="room-janswer-l">└ answer · </span>{ask.answer}</div>
          </>
        : isAsk
          ? <JournalAskQuestion text={step.detail!} tone={jTone(step.tone)} />
          : <div className={`room-jdetail room-jdetail-${jTone(step.tone)}`}>{step.detail}</div>)}
      {isAsk && askAnswerable && !step.answer && !muted && !step.planConfirmation && <AskAnswerBar escalation={step.reviewEscalation === true} />}
      {step.modelCall && (
        <div className="room-jmodel">
          <span className="room-jmodel-l">└ via · </span>
          <span className="room-jmodel-model">{step.modelCall.model ?? "model"}</span>
          {step.modelCall.tokens != null && step.modelCall.tokens > 0 && <span className="room-jmodel-x"> · {formatTokens(step.modelCall.tokens)} tokens</span>}
          {step.modelCall.costUsd != null && <span className="room-jmodel-x"> · {formatCostUsd(step.modelCall.costUsd)}</span>}
        </div>
      )}
      {step.plan.length > 0 && (
        <div className="room-jplan-card">
          {planCard
            ? <PlanChecklistCard plan={planCard} />
            : <PlanLiteCard subtasks={step.plan} version={planVersion} superseded={planSuperseded} />}
        </div>
      )}
      {step.agents.length > 0 && <div className="room-jagents"><AgentSection group={journalAgentsGroup(step)} /></div>}
      {step.deferred.length > 0 && <div className="room-jdeferred">{step.deferred.map((d) => <span key={d.subtaskId} className="room-jdefer">{d.subtaskId} · waiting on {d.waitingOn.join(", ")}</span>)}</div>}
    </div>
  );
}

/** The reviewer's verdict card under a REVIEW beat — COLLAPSED by default to one line (badge + rationale + chevron)
 *  so a run with several verdicts stays scannable; expanding reveals the evidence-attached issues and the independence
 *  line — "independent agent · claude-code" with a deep-link into the reviewer's OWN run, or "model critic —
 *  independently prompted" when the verdict came from the in-process critic. The WHOLE card toggles (open or closed) —
 *  clicking the expanded body collapses it again; only the deep-link button opts out. */
function ReviewVerdictCard({ review }: { review: JournalReviewVerdict }) {
  const openDrawer = useRoomDrawer();
  const run = useContext(RunActionsContext);
  const [open, setOpen] = useState(false);
  const n = review.issues.length;
  const reviewerRunId = review.reviewerRunId ?? null;
  return (
    <div className={`room-jverdict room-jverdict-${review.approved ? "ok" : "warn"}`} data-open={open} onClick={() => setOpen((v) => !v)}>
      <button type="button" className="room-jverdict-head" aria-expanded={open}>
        <span className="room-jverdict-badge">{review.approved ? "✓ approved" : `⚠ flagged${n > 0 ? ` · ${n} issue${n === 1 ? "" : "s"}` : ""}`}</span>
        <span className="room-jverdict-rationale" data-clamp={!open || undefined}>{review.rationale}</span>
        <Sym n={open ? "chevron-down" : "chevron-right"} s={12} cls="room-jverdict-chev" />
      </button>
      {open && <>
        {review.issues.map((issue, i) => <div key={i} className="room-jverdict-issue">└ {issue}</div>)}
        <div className="room-jverdict-via">
          <span className="room-jmodel-l">└ via · </span>
          {reviewerRunId
            ? <>
                <span className="room-jmodel-model">independent agent · {review.reviewerHarness ?? "agent"}</span>
                <span className="room-jmodel-x"> — a real {review.scope === "plan" ? "grounded plan" : "output"} review</span>
              </>
            : <>
                <span className="room-jmodel-model">a second AI</span>
                <span className="room-jmodel-x"> — an independent {review.scope} review</span>
              </>}
          {run && reviewerRunId && (
            <button className="room-jverdict-open" onClick={(e) => { e.stopPropagation(); openDrawer({ kind: "agent", agent: reviewerCard(review, reviewerRunId), runId: run.runId }); }}>
              view reviewer run <Sym n="chevron-right" s={11} />
            </button>
          )}
        </div>
      </>}
    </div>
  );
}

/** An ASK beat's question text — long questions CLAMP to a few lines with a "show more" toggle, so a parked card
 *  (especially an older run's unbounded escalation prose) never walls the timeline. Short questions render plain. */
function JournalAskQuestion({ text, tone }: { text: string; tone: string }) {
  const [expanded, setExpanded] = useState(false);
  const long = text.length > 280;
  return (
    <div className={`room-jdetail room-jdetail-${tone}`}>
      <span data-clamp={(long && !expanded) || undefined} className="room-jask-q">{text}</span>
      {long && <button type="button" className="room-jask-more" onClick={() => setExpanded((v) => !v)}>{expanded ? "show less" : "show more"}</button>}
    </div>
  );
}

/** The INLINE answer bar under a PENDING ask beat — the run page's own answer surface, so a parked run is operable
 *  right where the question appears (previously the sole surface was the conversation card, invisible from here).
 *  Posts to the run-scoped ask/answer endpoint, which resolves the SAME durable wait the card's Answer button does
 *  (first answer wins). A review-gate ESCALATION adds the one-shot "Approve anyway" quick action — the 'approve'
 *  reply the gate reads as absolution; any typed text is guidance the supervisor's next decide reads. */
function AskAnswerBar({ escalation }: { escalation: boolean }) {
  const run = useContext(RunActionsContext);
  const queryClient = useQueryClient();
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  // The answer folds onto the ask's durable outcome only at the NEXT supervisor turn, so the refetch right after a
  // successful POST still shows the ask "unanswered". Latch locally once the wait resolves — the bar reads "answer
  // sent — resuming…" instead of silently staying live (which invited a confusing second submit).
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (!run || run.isTerminal) return null;

  const send = async (answer: string) => {
    if (!answer.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const result = await sessionsApi.answerRunAsk(run.runId, answer.trim());
      if (result == null) {
        setError("Nothing to answer here — the question was already settled, or this ask has no answer surface.");
      } else {
        setSent(true);
        setText("");
        if (!result.resumed) setError("Already answered on another surface — the run is resuming.");
      }
      await queryClient.invalidateQueries({ queryKey: ["run-journal"] });
      await queryClient.invalidateQueries({ queryKey: ["run-room"] });
    } catch {
      setError("Could not send your answer — try again.");
    } finally {
      setBusy(false);
    }
  };

  if (sent) {
    return (
      <div className="room-jask-bar">
        <span className="room-jask-sent"><Sym n="check" s={11} /> {error ?? "Answer sent — the run is resuming…"}</span>
      </div>
    );
  }

  return (
    <div className="room-jask-bar">
      <input
        className="room-jask-input"
        placeholder={escalation ? "Describe what to do instead…" : "Type your answer…"}
        value={text}
        disabled={busy}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => { if (e.key === "Enter" && !e.nativeEvent.isComposing) void send(text); }}
      />
      <button type="button" className="room-jask-btn room-jask-send" disabled={busy || text.trim().length === 0} onClick={() => void send(text)}>
        Answer
      </button>
      {escalation && (
        <button type="button" className="room-jask-btn room-jask-approve" disabled={busy} title="Proceed with the blocked decision despite the review (one-shot)" onClick={() => void send("approve")}>
          <Sym n="check" s={11} /> Approve anyway
        </button>
      )}
      {error && <span className="room-jask-err">{error}</span>}
    </div>
  );
}

/** A minimal drawer card for the reviewer's OWN run — enough identity for the terminal / trace drawer to load by id. */
function reviewerCard(review: JournalReviewVerdict, reviewerRunId: string): RoomAgentCard {
  return { agentRunId: reviewerRunId, label: review.scope === "plan" ? "plan reviewer" : "reviewer", status: "Succeeded", harness: review.reviewerHarness ?? null };
}

function jTone(t: string): string { return t === "Success" ? "ok" : t === "Warning" ? "warn" : t === "Error" ? "err" : "info"; }
function jKindLabel(k: string): string { return k === "model_call" ? "model" : k; }
/** The semantic pill for a supervisor decision verb — PLAN / DISPATCH / ASK / MERGE / RETRY / RESOLVE (rendered uppercase by CSS), so a beat says WHAT was decided, not a generic "decision". The backend emits the lowercase/snake_case DecisionKind (plan / spawn / ask_human / …); lowercased here for robustness. Unknown/missing verb falls back to "decision". */
/** The supervisor-DISTINCTIVE decision verbs — a beat carrying one means the turn is supervisor-orchestrated (the actor
 *  lane reads "Supervisor"); a run with only node beats reads "Workflow". A supervisor's plan is its own first decision,
 *  so an ungated supervisor observed live between its plan and its spawn has ONLY a plan beat — "plan" must stay here or
 *  that window would misread "Workflow". The parallel map verbs are kept DISTINCT so they don't imply a supervisor: a
 *  flow.map planner beat is "map_plan" (not "plan") and its fan-out is "dispatch" (not "spawn") — both render the same
 *  PLAN / DISPATCH pill via jVerbKey, but neither is in this set, so a pure map run correctly reads "Workflow". */
const SUPERVISOR_VERBS = new Set(["plan", "spawn", "merge", "ask_human", "retry", "resolve", "stop"]);
function jVerbKey(verb: string | null | undefined): string {
  switch ((verb ?? "").toLowerCase()) {
    case "plan": return "plan";           // a supervisor plan
    case "map_plan": return "plan";       // a flow.map planner node — same PLAN pill, but not supervisor-distinctive
    case "spawn": return "dispatch";      // a supervisor spawn
    case "dispatch": return "dispatch";   // a flow.map node's fan-out — same DISPATCH pill
    case "retry": return "retry";
    case "ask_human": return "ask";
    case "merge": return "merge";
    case "resolve": return "resolve";
    case "stop": return "stop";
    case "review": return "review";     // an independent reviewer's verdict beat — the adversarial exchange
    case "revise": return "revise";     // the producer's revise round against the reviewer's / oracle's feedback
    default: return "decision";
  }
}
function jTime(iso: string): string { const d = new Date(iso); return Number.isNaN(d.getTime()) ? "" : d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }); }

/** Recover an ASK beat's question from its folded "{question} — {answer}" detail using the KNOWN structured answer:
 *  locate the answer at the end of the detail and strip it plus the trailing em-dash separator, so the question reads
 *  clean and the answer renders on its own line. Precise (no ambiguous em-dash split) — the question may itself contain an
 *  em-dash. If the answer isn't found in the detail (a format drift), the whole detail stays as the question (the answer
 *  line still shows step.answer). */
function askParts(detail: string | null | undefined, answer: string): { question: string; answer: string } {
  const d = (detail ?? "").trim();
  const a = answer.trim();
  const i = d.lastIndexOf(a);
  const question = i > 0 ? d.slice(0, i).replace(/\s*—\s*$/, "").trim() : d;
  return { question, answer: a };
}

/** One inner block, rendered by type as a Codex-style detail row / card. */
function InnerBlock({ block, pdById, onOpenTrace }: { block: RoomBlock; pdById: Map<string, PendingDecision>; onOpenTrace?: () => void }) {
  if (block.type === "stat") return <StatRow stat={block as StatBlock} />;
  if (block.type === "plan_checklist") return <PlanChecklistCard plan={block as PlanChecklistBlock} />;
  if (block.type === "agent_group") return <AgentSection group={block as AgentGroupBlock} />;
  if (block.type === "delivery") return <PrCard delivery={block as DeliveryBlock} />;
  if (block.type === "final_answer") return <FinalAnswer answer={block as FinalAnswerBlock} />;
  if (block.type === "live_activity") return <LiveTicker live={block as LiveActivityBlock} />;
  if (block.type === "diagnostic") return <ErrorCard diag={block as DiagnosticBlock} onOpenTrace={onOpenTrace} />;
  if (block.type === "decision") {
    const d = block as DecisionBlock;
    const liveD = pdById.get(d.decisionId);
    return liveD ? <div className="room-decision"><DecisionCard decision={liveD} /></div> : <DecisionPreview decision={d} />;
  }
  return <p className="room-para room-muted">{describeUnknown(block)}</p>;
}

/** The rich final answer — the closing text, then attachments grouped by kind: inline images, file links (preview / download), and the PR. */
function FinalAnswer({ answer }: { answer: FinalAnswerBlock }) {
  const openDrawer = useRoomDrawer();
  const run = useContext(RunActionsContext);
  const atts = answer.attachments ?? [];
  const images = atts.filter((a) => a.kind === "Image");
  const files = atts.filter((a) => a.kind === "FileLink");
  const prs = atts.filter((a) => a.kind === "Pr");

  // A degraded stop (the supervisor gave up — no-decision / no-model — no work delivered) is NOT a success: render a
  // neutral "Stopped" card with an alert glyph, not the green "Result", so its own "stopping cleanly" text isn't dressed
  // up as done. The run status is still a clean terminal Success at the engine level; only the outcome reads degraded.
  const degraded = answer.degraded === true;

  return (
    <div className={`room-final${degraded ? " room-final-degraded" : ""}`}>
      <div className="room-final-head"><Sym n={degraded ? "alert" : "check"} s={13} cls="room-final-ic" /> {degraded ? "Stopped" : "Result"}</div>
      {answer.text && <p className="room-final-text"><Inline text={answer.text} /></p>}
      {images.length > 0 && (
        <div className="room-final-gallery">
          {images.map((a, i) => <a key={i} href={a.url ?? a.previewUrl ?? "#"} target="_blank" rel="noreferrer"><img className="room-final-img" src={a.previewUrl ?? a.url ?? ""} alt={a.label} /></a>)}
        </div>
      )}
      {files.length > 0 && (
        <div className="room-final-files">
          {files.map((a, i) => (
            <button className="room-final-file" key={i} disabled={!run} onClick={() => run && openDrawer({ kind: "file", runId: run.runId, path: a.label, agentRunId: a.agentRunId ?? undefined })}>
              <Sym n="file" s={13} cls="room-final-file-ic" />
              <span className="room-final-file-name">{a.label}</span>
              {a.producer && <span className="room-final-file-by">· from {a.producer}</span>}
              <Sym n="chevron-right" s={12} cls="room-final-file-caret" />
            </button>
          ))}
        </div>
      )}
      {prs.map((a, i) => (
        <a key={i} className="room-pr-btn room-final-pr" href={a.url ?? "#"} target="_blank" rel="noreferrer"><Sym n="pr" s={14} /> {a.label}</a>
      ))}
    </div>
  );
}

/** The live "working…" indicator — a pulsing dot + the streaming activity line. Kept for any live_activity block that
 *  reaches InnerBlock directly; the running turn now unifies it into {@link LiveRunBar}. */
function LiveTicker({ live }: { live: LiveActivityBlock }) {
  return (
    <div className="room-live">
      <span className="room-live-dot" />
      <span className="room-live-text">{live.text}</span>
    </div>
  );
}

/** The destructive Stop control for a running turn — confirms before firing, then cancels the run + kills in-flight
 *  agents. Steady red (no pulse): the kill switch stays stable to click while the LiveRunBar's dot carries the live
 *  signal. Rendered inside {@link LiveRunBar}, not the footer, so Stop always sits with the progress it stops. */
function StopButton({ runId }: { runId: string }) {
  const cancel = useCancelRun(runId);
  const confirm = useConfirm();

  const onStop = async () => {
    const ok = await confirm({ title: "Stop this run?", message: "Cancels the run and kills any in-flight agents. You can Continue it later to resume where it stopped, or Re-run a fresh copy.", confirmLabel: "Stop run", cancelLabel: "Keep running", destructive: true });
    if (ok) cancel.mutate();
  };

  return <button className="room-btn-stop" onClick={() => void onStop()} disabled={cancel.isPending}><i className="room-stop-sq" /> {cancel.isPending ? "Stopping…" : "Stop"}</button>;
}

/** The running turn's ONE live control — a bar pinned (position: sticky) to the bottom of the scroll while the turn runs,
 *  so the live progress and the Stop button travel together and stay one click away no matter how far the content scrolls.
 *  Carries the single running pulse (dot), the latest activity line, the ticking elapsed, and the Stop button. Unmounts
 *  when the turn goes terminal — the footer's Continue/Re-run/View trace/Open PR take over. */
function LiveRunBar({ turn }: { turn: AssistantTurnBlock }) {
  const { activity, canStop } = liveRunSummary(turn);

  return (
    <div className="room-livebar">
      <span className="room-livebar-dot" />
      <div className="room-livebar-body">
        <div className="room-livebar-head"><span className="room-livebar-label">Working</span>{activity && <> · <span className="room-livebar-text">{activity}</span></>}</div>
        {turn.durationMs != null && <div className="room-livebar-meta">running {formatDurationMs(turn.durationMs)}</div>}
      </div>
      {canStop && <StopButton runId={turn.runId} />}
    </div>
  );
}

/** The live model output as it streams — a progressive text block fed by the run's SSE ledger tail (interaction.delta),
 *  shown only while the turn is live. Renders nothing until the first token lands; the finished call settles into its
 *  model-call row when the poll catches up. */
function TurnLiveStream({ runId }: { runId: string }) {
  const text = useRunRoomStream(runId);
  if (!text) return null;

  return (
    <div className="room-stream">
      <span className="room-stream-head"><span className="room-live-dot" />Streaming</span>
      <p className="room-stream-text">{text}<span className="room-stream-caret" /></p>
    </div>
  );
}

/** The generic collapsible detail-row shell — icon + label · detail + chevron, with expandable children. */
function DetailRow({ icon, iconTone, label, detail, children, defaultOpen }: { icon: SymName; iconTone?: string; label: string; detail?: ReactNode; children?: ReactNode; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen ?? false);
  const expandable = children != null;

  return (
    <div className="room-row">
      <button className="room-row-head" onClick={() => expandable && setOpen((o) => !o)} disabled={!expandable} aria-expanded={expandable ? open : undefined}>
        <Sym n={icon} s={14} cls={`room-row-ic${iconTone ? ` room-ic-${iconTone}` : ""}`} />
        <span className="room-row-label">{label}</span>
        {detail != null && <><span className="room-row-mid">·</span><span className="room-row-detail">{detail}</span></>}
        {expandable && <span className="room-row-caret"><Sym n={open ? "chevron-down" : "chevron-right"} s={13} /></span>}
      </button>
      {open && expandable && <div className="room-row-body">{children}</div>}
    </div>
  );
}

/** A stat row — Plan / Files changed / Tools / Reasoning, each with kind-specific expanded content. */
function StatRow({ stat }: { stat: StatBlock }) {
  const items = stat.items ?? [];
  const kind = stat.kind;
  const icon = statIcon(kind);
  const detail = kind === "files" ? <DiffStat text={stat.detail ?? ""} /> : stat.detail;

  if (kind === "reasoning") {
    return (
      <DetailRow icon={icon} iconTone="accent" label={stat.label} detail={stat.detail}>
        {items.length > 0 && <div className="room-reason"><Inline text={items.map((it) => it.text).join("\n\n")} /></div>}
      </DetailRow>
    );
  }

  if (kind === "subtasks") {
    return (
      <DetailRow icon={icon} label={stat.label} detail={detail}>
        {items.length > 0 && (
          <ol className="room-subtasks">
            {items.map((it, i) => (
              <li key={i}><span className="room-subtask-n">{i + 1}.</span><span><Inline text={it.text} /></span></li>
            ))}
          </ol>
        )}
      </DetailRow>
    );
  }

  if (kind === "files") {
    return (
      <DetailRow icon={icon} label={stat.label} detail={detail}>
        {items.length > 0 && (
          <div className="room-files">
            {items.map((it, i) => <FileRow key={i} item={it} />)}
          </div>
        )}
      </DetailRow>
    );
  }

  // tools (and any future kind) — a simple itemized list
  return (
    <DetailRow icon={icon} label={stat.label} detail={detail}>
      {items.length > 0 && (
        <div className="room-tools">
          {items.map((it, i) => (
            <div className="room-tool" key={i}>
              <span className="room-tool-text"><Inline text={it.text} /></span>
              {it.detail && <span className={`room-tool-detail${it.tone === "Success" ? " room-good" : it.tone === "Error" ? " room-danger" : ""}`}>{it.detail}</span>}
            </div>
          ))}
        </div>
      )}
    </DetailRow>
  );
}

/** One changed-file row in the Files-changed panel — path + per-file +/− (color-split), degrading when absent. */
function FileRow({ item }: { item: StatItem }) {
  const openDrawer = useRoomDrawer();
  const run = useContext(RunActionsContext);
  return (
    <button className="room-file" disabled={!run} onClick={() => run && openDrawer({ kind: "file", runId: run.runId, path: item.text })}>
      <span className="room-file-path">{item.text}</span>
      {item.detail && <span className="room-file-from">{item.detail}</span>}
      <Sym n="chevron-right" s={11} cls="room-file-caret" />
    </button>
  );
}

/** The run's plan as a live checklist — the whole current version, one checkable row per item: state icon ·
 *  title · contract chips (kind / dependencies / acceptance / criteria / attempts) · state word · Details. The
 *  backend owns every string; unknown states render neutral. Questions/assumptions are read-only here. */
/**
 * A read-only plan card for a version that carries no rich block — a SUPERSEDED supervisor plan, or a workflow
 * planner's plan that was never up for confirmation. It reuses the room plan-card frame (same border / header / rows)
 * so the version looks like the plan it was, built from the beat's OWN authored subtasks. No per-item status (a
 * superseded version never ran), no edit box or approve/request-changes buttons (there is nothing to confirm — a
 * superseded version's outcome shows on the ask beat that follows). Rows carry NO status marker — not the rich card's
 * checkbox (a read-only plan has nothing to check off, so a checkbox would falsely read as actionable), just the numbered
 * title. The "superseded" chip shows only for an earlier version; the current plan rendered
 * lite (no rich block available) is not superseded, so it carries no chip.
 */
function PlanLiteCard({ subtasks, version, superseded }: { subtasks: JournalSubtask[]; version?: number; superseded?: boolean }) {
  return (
    <div className="room-plan">
      <div className="room-plan-head">
        <Sym n="list" s={14} cls="room-plan-ic" />
        <span className="room-plan-title">Plan</span>
        {version != null && <span className="room-plan-ver">v{version}</span>}
        {superseded && <span className="room-plan-superseded">superseded</span>}
      </div>
      <div className="room-plan-rows">
        {subtasks.map((s, i) => (
          // Key by row index, not subtaskId — the plan validator tolerates duplicate subtask ids (a degenerate flat
          // plan), so subtaskId can collide; this static read-only list never reorders, so the index is a stable key.
          <div key={i} className="room-prow">
            <div className="room-prow-main"><div className="room-prow-title">{i + 1}. {s.title}</div></div>
          </div>
        ))}
      </div>
    </div>
  );
}

function PlanChecklistCard({ plan }: { plan: PlanChecklistBlock }) {
  const run = useContext(RunActionsContext);
  const queryClient = useQueryClient();
  const awaiting = plan.status === "AwaitingConfirmation";
  // The card is answerable only on a live turn — a superseded / finished turn renders the same card read-only.
  const confirmable = awaiting && run != null && !run.isTerminal;
  const assumptions = plan.assumptions ?? [];
  const questions = plan.questions ?? [];
  const hasFooter = assumptions.length > 0 || questions.length > 0 || plan.hasPriorVersions;

  // Question choices default to the planner's recommendation; the free-text note is the operator's own words.
  const [choices, setChoices] = useState<Record<string, string>>(() => recommendedChoices(questions));
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // A revised plan version re-gates in place (same block id, no remount) — reset the form to ITS
  // recommendations. Render-time adjustment (not an effect): React re-renders immediately, no stale frame.
  const [formVersion, setFormVersion] = useState(plan.version);
  if (formVersion !== plan.version) {
    setFormVersion(plan.version);
    setChoices(recommendedChoices(questions));
    setNote("");
    setError(null);
  }

  const feedback = () =>
    composePlanFeedback(
      questions
        .map((q) => {
          const opt = (q.options ?? []).find((o) => o.id === choices[q.id]);
          return opt ? { question: q.question, choice: opt.label } : null;
        })
        .filter((c): c is { question: string; choice: string } => c != null),
      note,
    );

  const answer = async (approve: boolean) => {
    if (!run) return;
    setBusy(true);
    setError(null);
    try {
      const result = await sessionsApi.confirmRunPlan(run.runId, { approve, feedback: feedback() || undefined });
      if (result == null) setError("Nothing left to confirm — the plan was already answered.");
      await queryClient.invalidateQueries({ queryKey: ["run-journal"] });
      await queryClient.invalidateQueries({ queryKey: ["run-room"] });
    } catch {
      setError("Could not send your answer — try again.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="room-plan">
      <div className="room-plan-head">
        <Sym n="list" s={14} cls="room-plan-ic" />
        <span className="room-plan-title">{plan.label}</span>
        <span className="room-plan-ver">v{plan.version}</span>
        {awaiting && <span className="room-plan-status">awaiting confirmation</span>}
        {plan.detail && <><span className="room-row-mid">·</span><span className="room-plan-detail">{plan.detail}</span></>}
      </div>
      <div className="room-plan-rows">
        {plan.items.map((it) => <PlanItemRow key={it.ordinal} it={it} />)}
      </div>
      {questions.length > 0 && (
        <div className="room-plan-questions">
          {questions.map((q) => (
            <PlanQuestionRow key={q.id} q={q} value={confirmable ? choices[q.id] : undefined} onSelect={confirmable ? (optId) => setChoices((c) => ({ ...c, [q.id]: optId })) : undefined} />
          ))}
        </div>
      )}
      {confirmable && (
        <div className="room-plan-confirm">
          <textarea
            className="room-plan-confirm-note"
            placeholder="Optional note — or describe the changes you want…"
            value={note}
            rows={2}
            onChange={(e) => setNote(e.target.value)}
            disabled={busy}
          />
          <div className="room-plan-confirm-row">
            {error && <span className="room-plan-confirm-err">{error}</span>}
            <span className="room-plan-foot-gap" />
            <button className="room-plan-confirm-btn room-plan-confirm-revise" disabled={busy || note.trim().length === 0} title={note.trim().length === 0 ? "Describe the changes you want first" : undefined} onClick={() => answer(false)}>
              Request changes
            </button>
            <button className="room-plan-confirm-btn room-plan-confirm-approve" disabled={busy} onClick={() => answer(true)}>
              <Sym n="check" s={12} /> Approve & run
            </button>
          </div>
        </div>
      )}
      {hasFooter && (
        <div className="room-plan-foot">
          {assumptions.length > 0 && <span className="room-plan-assume" title={assumptions.join("\n")}><Sym n="edit" s={11} /> {assumptions.length === 1 ? assumptions[0] : `${assumptions.length} assumptions`}</span>}
          <span className="room-plan-foot-gap" />
          {plan.hasPriorVersions && <span className="room-plan-prior">v{plan.version - 1} superseded</span>}
        </div>
      )}
    </div>
  );
}

/** One checklist row. Opens the latest attempt's agent terminal from Details when an agent is linked. */
function PlanItemRow({ it }: { it: PlanChecklistItem }) {
  const openDrawer = useRoomDrawer();
  const agentCards = useContext(PlanAgentCardsContext);
  const run = useContext(RunActionsContext);
  const tone = planStateTone(it.state);
  const criteria = it.acceptanceCriteria ?? [];
  const deps = it.dependsOn ?? [];
  const canOpen = run != null && it.agentRunId != null;

  return (
    <div className="room-prow">
      <span className={`room-prow-ic room-prow-ic-${tone}`}><Sym n={planStateIcon(it.state) as SymName} s={15} /></span>
      <div className="room-prow-main">
        <div className="room-prow-title">{it.ordinal}. {it.title}</div>
        {(it.kind || deps.length > 0 || it.acceptanceLabel || criteria.length > 0 || it.attempts > 1) && (
          <div className="room-prow-chips">
            {it.kind && <span className="room-pchip">{it.kind}</span>}
            {planDepsLabel(deps) && <span className="room-prow-deps">{planDepsLabel(deps)}</span>}
            {it.acceptanceLabel && (
              <span className={`room-pchip room-pchip-${it.acceptancePassed === true ? "ok" : it.acceptancePassed === false ? "err" : "check"}`} title={it.acceptanceDetail ?? undefined}>
                <Sym n={it.acceptanceKind === "ArtifactPresent" ? "file" : "terminal"} s={10} /> {it.acceptanceLabel}
              </span>
            )}
            {criteria.map((c, i) => <span key={i} className="room-pchip">{c}</span>)}
            {it.attempts > 1 && <span className="room-pchip room-pchip-tries">×{it.attempts} attempts</span>}
          </div>
        )}
        {/* A FAILED plan item is the ACCEPTANCE check failing, NOT the agent — the agent may have Succeeded (its card
            reads DONE) while the acceptance test couldn't verify the artifact (e.g. "no-branch-or-repo" = the harness
            couldn't reach the workspace branch). Surface that reason inline so the row doesn't read as "the agent failed". */}
        {it.acceptancePassed === false && it.acceptanceDetail && (
          <div className="room-prow-accept-err">acceptance failed · {it.acceptanceDetail}</div>
        )}
      </div>
      <span className={`room-prow-state room-prow-state-${tone}`}>{planStateWord(it.state)}</span>
      <button
        className="room-prow-act"
        disabled={!canOpen}
        onClick={() => canOpen && openDrawer({ kind: "agent", runId: run!.runId, agent: agentCards.get(it.agentRunId!) ?? { agentRunId: it.agentRunId!, label: it.title, status: planAgentStatus(it.state) } })}
      >
        Details <Sym n="chevron-right" s={11} />
      </button>
    </div>
  );
}

/** A planner question rendered read-only — the choose-a-direction preview (the interactive form arrives with the plan gate). */
/** Each question's planner-recommended option id — the confirm form's default selection. */
function recommendedChoices(questions: RoomPlanQuestion[]): Record<string, string> {
  return Object.fromEntries(
    questions
      .map((q) => {
        const rec = (q.options ?? []).find((o) => o.recommended);
        return rec ? ([q.id, rec.id] as const) : null;
      })
      .filter((e): e is readonly [string, string] => e != null),
  );
}

function PlanQuestionRow({ q, value, onSelect }: { q: RoomPlanQuestion; value?: string; onSelect?: (optionId: string) => void }) {
  const opts = q.options ?? [];
  return (
    <div className="room-plan-q">
      <div className="room-plan-q-text"><Sym n="sparkle" s={12} cls="room-ic-accent" /> {q.question}</div>
      {opts.length > 0 && (
        <div className="room-chips">
          {opts.map((o) =>
            onSelect ? (
              <button key={o.id} className={`room-chip room-chip-pick ${value === o.id ? "room-chip-accent" : "room-chip-plain"}`} onClick={() => onSelect(o.id)}>
                {o.label}{o.recommended ? " · recommended" : ""}
              </button>
            ) : (
              <span key={o.id} className={`room-chip ${o.recommended ? "room-chip-accent" : "room-chip-plain"}`}>{o.label}{o.recommended ? " · recommended" : ""}</span>
            ),
          )}
        </div>
      )}
    </div>
  );
}

const AGENT_PIN_LIMIT = 6;

/** Agents — the design's compact "Work · N agents" panel: a counts header, then one row per agent (status dot · name ·
 *  time · state · quiet action). Failed / timed-out agents pin to the top; the rest collapse behind "Show N more". */
function AgentSection({ group }: { group: AgentGroupBlock }) {
  const [expanded, setExpanded] = useState(false);

  const agents = [...group.agents].sort((a, b) => agentSortRank(a.status) - agentSortRank(b.status));
  const counts = agentCounts(group.agents);

  const collapsible = agents.length > AGENT_PIN_LIMIT;
  const shown = collapsible && !expanded ? agents.slice(0, AGENT_PIN_LIMIT) : agents;
  const hidden = agents.length - shown.length;

  return (
    <div className="room-work">
      <div className="room-work-head">
        <span className="room-work-title">{group.title}</span>
        <span className="room-row-mid">·</span>
        <span className="room-work-sub">{group.agents.length} agent{group.agents.length === 1 ? "" : "s"}</span>
        {counts && <span className="room-work-counts">{counts}</span>}
      </div>
      <div className="room-work-rows">
        {shown.map((a) => <AgentRow key={a.agentRunId} a={a} />)}
      </div>
      {collapsible && (
        <button className="room-work-more" onClick={() => setExpanded((v) => !v)}>
          {expanded ? "Show less" : `Show ${hidden} more`} <Sym n={expanded ? "chevron-up" : "chevron-down"} s={13} />
        </button>
      )}
    </div>
  );
}

/** One agent as a compact row — status dot · name · files · time · state word · quiet action; opens the agent (its files + terminal) in the side drawer. */
function AgentRow({ a }: { a: RoomAgentCard }) {
  const openDrawer = useRoomDrawer();
  const run = useContext(RunActionsContext);
  const cls = agentTone(a.status);
  const running = a.status === "Running";
  const queued = a.status === "Queued" || a.status === "Pending";
  const action = running ? "Open terminal" : queued ? "View" : cls === "err" ? "View trace" : "Details";
  const fileCount = a.changedFiles?.length ?? a.filesChanged ?? 0;
  const tokens = a.tokens ?? 0;

  return (
    <div className="room-arow-wrap">
      <button className="room-arow" data-queued={queued || undefined} disabled={!run} onClick={() => run && openDrawer({ kind: "agent", agent: a, runId: run.runId })}>
        <span className={`room-adot room-adot-${cls}`} />
        <span className="room-arow-name" title={a.summary ?? a.label}>{a.label}</span>
        {(tokens > 0 || fileCount > 0 || a.model || a.harness || a.resumed || a.review) && (
          <span className="room-arow-meta">
            {a.harness && <span className={`room-arow-metaitem room-arow-harness room-arow-hn-${harnessTint(a.harness)}`} title={`Harness · ${a.harness}`}><Sym n="terminal" s={11} cls="room-arow-metaic room-arow-hic" /></span>}
            {tokens > 0 && <span className="room-arow-metaitem" title={`${tokens.toLocaleString()} tokens`}><Sym n="cpu" s={10} cls="room-arow-metaic" /> {formatTokens(tokens)} tokens</span>}
            {a.model && <span className="room-arow-metaitem room-arow-model" title={`Model · ${a.model}`}><Sym n="sparkle" s={10} cls="room-arow-metaic" /> {a.model}</span>}
            {fileCount > 0 && <span className="room-arow-metaitem" title={`${fileCount} file${fileCount === 1 ? "" : "s"} changed`}><Sym n="file" s={10} cls="room-arow-metaic" /> {fileCount} {fileCount === 1 ? "file" : "files"}</span>}
            {a.resumed && <span className="room-arow-metaitem room-arow-resumed" title="Continued its earlier conversation (the retry resumed the session)"><Sym n="rerun" s={10} cls="room-arow-metaic" /> resumed</span>}
            {a.review && <span className={`room-arow-metaitem room-arow-review-${a.review.approved ? "ok" : "warn"}`} title={`Independent review — ${a.review.approved ? "approved" : "flagged"}: ${a.review.rationale}`}>{a.review.approved ? "✓ reviewed" : `⚠ flagged${a.review.issues.length > 0 ? ` · ${a.review.issues.length}` : ""}`}</span>}
          </span>
        )}
        <span className="room-arow-time">{a.durationMs != null ? formatDurationMs(a.durationMs) : "—"}</span>
        <span className={`room-arow-state room-arow-state-${cls}`}>{agentStatusWord(a.status)}</span>
        <span className="room-arow-act">{action} <Sym n="chevron-right" s={11} /></span>
      </button>
      {cls === "err" && a.error && <div className="room-arow-err" title={a.error}><Sym n="alert" s={11} cls="room-arow-erric" /> {a.error}</div>}
    </div>
  );
}

/** The delivered change set — the terracotta PR card. */
function PrCard({ delivery }: { delivery: DeliveryBlock }) {
  return (
    <div className="room-pr">
      <span className="room-pr-av"><Sym n="pr" s={16} /></span>
      <div className="room-pr-main">
        <div className="room-pr-title"><span className="room-pr-name">{delivery.title}</span>{delivery.reference && <span className="room-pr-ref">{delivery.reference}</span>}</div>
        {(delivery.branchHead || delivery.checks) && (
          <div className="room-pr-sub">
            {delivery.branchHead && <span className="room-pr-branch"><Sym n="branch" s={11} /> {delivery.branchHead} → {delivery.branchBase ?? "main"}</span>}
            {delivery.checks && <><span className="room-row-mid">·</span><span className={delivery.checksOk ? "room-good" : delivery.checksOk === false ? "room-danger" : "room-muted"}>{delivery.checks}</span></>}
          </div>
        )}
      </div>
      {delivery.url && <a className="room-pr-btn" href={delivery.url} target="_blank" rel="noreferrer">View PR</a>}
    </div>
  );
}

/** The rich failure diagnostic — humanized title + cause + typed remediation + the raw error behind a toggle. */
function ErrorCard({ diag, onOpenTrace }: { diag: DiagnosticBlock; onOpenTrace?: () => void }) {
  const [showRaw, setShowRaw] = useState(false);
  const actions = diag.actions ?? [];

  return (
    <div className="room-err">
      <span className="room-err-av"><Sym n="alert" s={16} /></span>
      <div className="room-err-main">
        {diag.title && <div className="room-err-title">{diag.title}</div>}
        <p className="room-err-text"><Inline text={diag.text} /></p>
        {actions.length > 0 && (
          <div className="room-err-actions">
            {actions.map((act, i) => (
              <button
                key={act.kind}
                className={i === 0 ? "room-btn-primary" : act.kind === "OpenTrace" ? "room-btn-ghost" : "room-btn"}
                disabled={!act.enabled || (act.kind === "OpenTrace" && !onOpenTrace)}
                title={act.disabledReason ?? undefined}
                // The raw run detail used to open in a modal; now "View trace" summons the companion pane on its trace mini-tab.
                onClick={act.kind === "OpenTrace" ? onOpenTrace : undefined}
              >
                {act.kind === "FixCredentials" && <Sym n="lock" s={13} />}
                {act.kind === "RerunTurn" && <Sym n="rerun" s={13} />}
                {act.kind === "OpenTrace" && <Sym n="terminal" s={13} />}
                {act.kind === "OpenTrace" ? "View trace" : act.label}
              </button>
            ))}
          </div>
        )}
        {diag.rawDetail && (
          <button className="room-err-raw-toggle" onClick={() => setShowRaw((s) => !s)}>
            <Sym n={showRaw ? "chevron-down" : "chevron-right"} s={12} /> {showRaw ? "Hide raw error" : "Show raw error"}
          </button>
        )}
        {showRaw && diag.rawDetail && <pre className="room-err-raw">{diag.rawDetail}</pre>}
      </div>
    </div>
  );
}

/** The decision / approval gate, styled to the design — an accent-left "Decision" card, or a warn "Approval required"
 *  card for a side-effecting approve gate. Read-only here (the live answerable card renders when the wait is pending). */
function DecisionPreview({ decision }: { decision: DecisionBlock }) {
  const approval = decision.shape === "approve_action" || (decision.options?.some((o) => o.sideEffecting) ?? false);
  const opts = decision.options ?? [];

  if (approval) {
    return (
      <div className="room-approval">
        <span className="room-approval-av"><Sym n="lock" s={15} /></span>
        <div className="room-approval-main">
          <div className="room-approval-title">{decision.question}</div>
          {opts.length > 0 && (
            <div className="room-chips">{opts.map((o, i) => <span key={o.id} className={`room-chip room-chip-${i === 0 ? "warn" : "plain"}`}>{o.label}</span>)}</div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="room-decide">
      <div className="room-decide-head"><Sym n="sparkle" s={14} cls="room-ic-accent" /><span className="room-decide-title">Decision</span></div>
      <p className="room-decide-q">{decision.question}</p>
      {opts.length > 0 && (
        <div className="room-chips">{opts.map((o, i) => <span key={o.id} className={`room-chip room-chip-${i === 0 ? "accent" : o.sideEffecting ? "danger" : "plain"}`}>{o.label}</span>)}</div>
      )}
    </div>
  );
}

/** The turn footer actions — the doing-actions first (Continue / Re-run / Open PR), then "Open canvas" LAST. Re-run
 *  confirms before firing. Stop is NOT here: while running it lives in the pinned LiveRunBar with the progress it stops.
 *  Capability-gated by the backend (never 422s). */
function TurnActions({ actions, turn, onOpenCanvas, onOpenRun }: { actions: RoomAction[]; turn: AssistantTurnBlock; onOpenCanvas?: () => void; onOpenRun: (runId: string) => void }) {
  const replay = useReplayRun();
  const cont = useContinueRun(turn.runId);
  const openPr = useOpenPullRequest(turn.runId);
  const confirm = useConfirm();
  const alert = useAlert();
  if (actions.length === 0) return null;

  const onRerun = async () => {
    const ok = await confirm({ title: "Re-run this turn?", message: "Starts a fresh attempt of this turn. The current result is kept in the turn's history.", confirmLabel: "Re-run", cancelLabel: "Cancel" });
    if (!ok) return;
    const result = await replay.mutateAsync(turn.runId);
    onOpenRun(result.runId);
  };

  // Resume the stopped/failed turn IN PLACE (same run id) — re-runs the interrupted frontier where it halted. If nothing
  // is left to resume (a stop that landed on a wave boundary), offer a fresh Re-run instead of a silent no-op.
  const onContinue = async () => {
    const res = await cont.mutateAsync();
    if (res.continued) return;   // resumed in place — the hook's invalidation refreshes the room to its now-live status
    const rerun = await confirm({ title: "Nothing to resume", message: "This turn has no interrupted step left to continue in place. Start a fresh Re-run instead?", confirmLabel: "Re-run", cancelLabel: "Not now" });
    if (rerun) { const result = await replay.mutateAsync(turn.runId); onOpenRun(result.runId); }
  };

  // Opens a real PR/MR for this turn's published branch(es) and jumps straight to it — mirrors PrCard's own
  // "View PR" external-link behavior, since there's nothing more to confirm here (the branch already exists).
  // A multi-repo run isolates each repo's failure (a missing credential scope, a rejected branch) into a Failed
  // disposition rather than throwing — surface it rather than silently doing nothing on click.
  const onOpenPr = async () => {
    const result = await openPr.mutateAsync();
    const url = result.pullRequests.find((p) => p.url)?.url;
    if (url) { window.open(url, "_blank", "noreferrer"); return; }
    const failure = result.pullRequests.find((p) => p.error);
    if (failure) await alert({ title: "Couldn't open the pull request", message: failure.error, variant: "error" });
  };

  // The doing-actions render first; the pane summon ("開啟Canvas") is always last (a quiet ghost). Stop is excluded — the
  // LiveRunBar owns it while running. Rerun shows ONLY when its capability is enabled (a finished turn shows Rerun). The
  // OpenTrace action is the backend's gate for offering the run's detail; it now summons the companion pane (canvas),
  // not the removed modal.
  const trace = actions.find((a) => a.kind === "OpenTrace");
  const doing = actions.filter((a) => a.kind !== "OpenTrace" && a.kind !== "Stop" && a.enabled);

  return (
    <div className="room-foot">
      {doing.map((a) => {
        if (a.kind === "Continue") return <button key={a.kind} className="room-btn-primary" onClick={() => void onContinue()} disabled={cont.isPending} title="Resume this turn where it stopped — re-runs the interrupted step, keeping the work already done."><Sym n="play" s={12} /> {cont.isPending ? "Resuming…" : a.label}</button>;
        if (a.kind === "RerunTurn") return <button key={a.kind} className="room-btn" onClick={() => void onRerun()} disabled={replay.isPending} title="Try again from scratch — a fresh attempt; the current result is kept in the turn's history."><Sym n="rerun" s={13} /> {replay.isPending ? "Rerunning…" : a.label}</button>;
        if (a.kind === "RerunFromNode") return <button key={a.kind} className="room-btn" title={a.disabledReason ?? undefined}><Sym n="branch" s={13} /> {a.label}</button>;
        if (a.kind === "OpenPullRequest") {
          if (a.url) return <a key={a.kind} className="room-btn" href={a.url} target="_blank" rel="noreferrer"><Sym n="pr" s={13} /> {a.label}</a>;
          return <button key={a.kind} className="room-btn" onClick={() => void onOpenPr()} disabled={openPr.isPending}><Sym n="pr" s={13} /> {openPr.isPending ? "Opening…" : a.label}</button>;
        }
        return null;
      })}
      {trace && onOpenCanvas && <button className="room-btn-ghost" onClick={onOpenCanvas} title="Open this turn's execution graph in the canvas">⧉ Open canvas</button>}
    </div>
  );
}

// ─── helpers ───

/** Render plain text with `backtick code` spans as inline code chips (the backend authors plain text). */
function Inline({ text }: { text: string }) {
  if (!text.includes("`")) return <>{text}</>;
  const parts = text.split(/(`[^`]+`)/g);
  return <>{parts.map((p, i) => (p.length > 1 && p.startsWith("`") && p.endsWith("`") ? <code key={i} className="room-code">{p.slice(1, -1)}</code> : <Fragment key={i}>{p}</Fragment>))}</>;
}

/** Color-split a diffstat string ("+148 −32 · 6 files") — additions green, deletions red, the rest muted. */
function DiffStat({ text }: { text: string }) {
  const toks = text.split(/(\s+)/);
  return (
    <span className="room-diffstat">
      {toks.map((t, i) => {
        if (/^\+\d/.test(t)) return <span key={i} className="room-good">{t}</span>;
        if (/^[−-]\d/.test(t)) return <span key={i} className="room-danger">{t}</span>;
        return <Fragment key={i}>{t}</Fragment>;
      })}
    </span>
  );
}

function toPhaseAgentRef(c: RoomAgentCard): PhaseAgentRef {
  return {
    // nodeId/iterationKey (the cell key) light up the terminal's attempt switcher — the card carries the pre-summed token
    // total, so forward it as inputTokens (the terminal sums input+output) rather than dropping it.
    agentRunId: c.agentRunId, nodeId: c.nodeId ?? null, iterationKey: c.iterationKey ?? "", status: c.status, label: c.label, role: c.role ?? null,
    assignedSubtask: c.assignedSubtask ?? null, model: c.model ?? null, inputTokens: c.tokens ?? null, outputTokens: null,
    durationMs: c.durationMs ?? null, toolCount: c.toolCount ?? null, costUsd: c.costUsd ?? null, filesChanged: c.filesChanged ?? null,
    changedFiles: c.changedFiles ?? null,
  };
}

const FAILED_AGENT = new Set(["Failed", "Cancelled", "TimedOut"]);

function agentTone(status: string): string {
  if (FAILED_AGENT.has(status)) return "err";
  if (status === "Running") return "run";
  if (status === "Queued" || status === "Pending") return "idle";
  return "ok";
}

/** The tint class for a harness's small glyph — codex vs claude read at a glance by colour; any other kind stays neutral. Matched loosely so a versioned kind ("codex-cli", "claude-code") still resolves. */
function harnessTint(harness: string): string {
  const h = harness.toLowerCase();
  if (h.includes("codex")) return "codex";
  if (h.includes("claude")) return "claude";
  return "other";
}

function agentStatusWord(status: string): string {
  if (status === "Succeeded") return "done";
  if (status === "Failed") return "failed";
  if (status === "Cancelled") return "cancelled";
  if (status === "TimedOut") return "timed out";
  if (status === "Running") return "running";
  if (status === "NeedsReview") return "review";
  return status.toLowerCase();
}

/** Sort order for the agent rows — failed / timed-out pin to the top, then running, then queued, then done. */
function agentSortRank(status: string): number {
  if (FAILED_AGENT.has(status)) return 0;
  if (status === "Running") return 1;
  if (status === "Queued" || status === "Pending") return 2;
  return 3;
}

/** The counts summary in the Work header — "9 running · 2 timed out · 1 done", each bucket dropped when empty. */
function agentCounts(agents: RoomAgentCard[]): string {
  const n = (pred: (s: string) => boolean) => agents.filter((a) => pred(a.status)).length;
  const buckets: [number, string][] = [
    [n((s) => s === "Running"), "running"],
    [n((s) => s === "Failed" || s === "Cancelled"), "failed"],
    [n((s) => s === "TimedOut"), "timed out"],
    [n((s) => s === "Queued" || s === "Pending"), "queued"],
    [n((s) => s === "Succeeded"), "done"],
    [n((s) => s === "NeedsReview"), "review"],
  ];
  return buckets.filter(([c]) => c > 0).map(([c, label]) => `${c} ${label}`).join(" · ");
}

function statusTone(status: WorkflowRunStatus, live: boolean): string {
  if (status === "Failure") return "err";
  if (status === "Success") return "ok";
  if (status === "Suspended") return "wait";
  if (status === "Cancelled") return "idle";
  return live ? "run" : "ok";
}

function pillIcon(tone: string): SymName {
  if (tone === "ok") return "check";
  if (tone === "err") return "x";
  if (tone === "wait") return "clock";
  return "dot";
}

function pillLabel(status: WorkflowRunStatus, live: boolean): string {
  // An in-flight turn is "Working" (or "Waiting" when parked on a human); a terminal turn uses the shared lexicon so the
  // Room and the Runs list speak the same words.
  if (live) return status === "Suspended" ? "Waiting" : "Working";

  return statusWord(status);
}

/** The turn's meta line after "Turn N" — the start time (when it ran) then the duration: " · Jun 29, 13:47 · 28m" (or " · running 38s" live). */
function turnMeta(turn: AssistantTurnBlock, nowMs: number, live: boolean): string {
  const parts: string[] = [];
  if (turn.at) parts.push(formatStartTime(turn.at, nowMs));
  if (turn.durationMs != null) parts.push(live ? `running ${formatDurationMs(turn.durationMs)}` : formatDurationMs(turn.durationMs));
  return parts.length ? ` · ${parts.join(" · ")}` : "";
}

/** The turn's start instant — "13:47" when it's today, else "Jun 29, 13:47" (with the year only when it isn't the current one). */
function formatStartTime(at: string, nowMs: number): string {
  const d = new Date(at);
  const now = new Date(nowMs);
  const time = d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  if (d.toDateString() === now.toDateString()) return time;
  const date = d.toLocaleDateString(undefined, { month: "short", day: "numeric", ...(d.getFullYear() === now.getFullYear() ? {} : { year: "numeric" }) });
  return `${date}, ${time}`;
}

function formatDurationMs(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

function execDotClass(status: ExecutionStepStatus): string {
  if (status === "Done") return "done";
  if (status === "Failed") return "failed";
  if (status === "Running") return "running";
  if (status === "Blocked") return "blocked";
  return "idle"; // Queued / Skipped / Pending
}

function execDotIcon(status: ExecutionStepStatus, deliver: boolean): SymName {
  if (status === "Done") return deliver ? "pr" : "check";
  if (status === "Failed") return "x";
  if (status === "Running") return "rerun";
  if (status === "Blocked") return "alert";
  return "dot"; // idle
}

/** The connector leg between two stages: solid after Done, animated after Running, dashed otherwise. */
function connKind(prev: ExecutionStepStatus, cur: ExecutionStepStatus): string {
  if (prev === "Done") return cur === "Running" ? "done-active" : "done";
  if (prev === "Running") return "active";
  return "pending";
}

const GENERIC_SUMMARY = new Set(["Working…", "Waiting for input.", ""]);

function statIcon(kind: string): SymName {
  if (kind === "subtasks") return "list";
  if (kind === "files") return "file";
  if (kind === "tools") return "terminal";
  if (kind === "reasoning") return "sparkle";
  return "list";
}

function describeUnknown(block: RoomBlock): string {
  const text = (block as { text?: string }).text;
  return text ?? `(${block.type})`;
}

// ─── icons (the design's exact SVG symbol paths, inlined for 1:1 fidelity) ───

type SymName =
  | "chevron-right" | "chevron-down" | "chevron-up" | "check" | "x" | "dot" | "file" | "terminal" | "sparkle"
  | "zap" | "lock" | "folder" | "send" | "rerun" | "more" | "alert" | "clock" | "link" | "branch"
  | "pr" | "stop" | "edit" | "list" | "cpu" | "download" | "square" | "square-check" | "square-x" | "play";

const ICONS: Record<SymName, { fill?: boolean; sw?: number; body: ReactNode }> = {
  play: { fill: true, body: <path d="M7 5v14l11-7z" /> },
  "chevron-right": { sw: 1.9, body: <path d="M9 18l6-6-6-6" /> },
  "chevron-down": { sw: 1.9, body: <path d="M6 9l6 6 6-6" /> },
  "chevron-up": { sw: 1.9, body: <path d="M18 15l-6-6-6 6" /> },
  check: { sw: 2.2, body: <path d="M20 6L9 17l-5-5" /> },
  x: { sw: 2.2, body: <path d="M18 6L6 18M6 6l12 12" /> },
  dot: { fill: true, body: <circle cx="12" cy="12" r="5" /> },
  file: { sw: 1.7, body: <><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6" /></> },
  terminal: { sw: 1.8, body: <><path d="M5 17l5-5-5-5" /><line x1="12" y1="19" x2="19" y2="19" /></> },
  sparkle: { sw: 1.6, body: <path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z" /> },
  zap: { sw: 1.7, body: <path d="M13 2L4 14h7l-1 8 9-12h-7z" /> },
  lock: { sw: 1.7, body: <><rect x="4" y="11" width="16" height="10" rx="2" /><path d="M8 11V7a4 4 0 0 1 8 0v4" /></> },
  folder: { sw: 1.7, body: <path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.5l-1.7-2H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2z" /> },
  send: { sw: 2, body: <><line x1="12" y1="19" x2="12" y2="5" /><path d="M5 12l7-7 7 7" /></> },
  rerun: { sw: 1.8, body: <><path d="M3 12a9 9 0 1 0 2.6-6.3L3 8" /><path d="M3 3v5h5" /></> },
  more: { fill: true, body: <><circle cx="5" cy="12" r="1.7" /><circle cx="12" cy="12" r="1.7" /><circle cx="19" cy="12" r="1.7" /></> },
  alert: { sw: 1.8, body: <><path d="M10.3 3.8L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.8a2 2 0 0 0-3.4 0z" /><line x1="12" y1="9" x2="12" y2="13" /><line x1="12" y1="17" x2="12" y2="17" /></> },
  clock: { sw: 1.7, body: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3.5 2" /></> },
  link: { sw: 1.7, body: <><path d="M10 13a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-1.5 1.5" /><path d="M14 11a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l1.5-1.5" /></> },
  branch: { sw: 1.8, body: <><line x1="6" y1="4" x2="6" y2="15" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="6" r="3" /><path d="M18 9a9 9 0 0 1-9 9" /></> },
  pr: { sw: 1.8, body: <><circle cx="6" cy="6" r="3" /><circle cx="6" cy="18" r="3" /><line x1="6" y1="9" x2="6" y2="15" /><circle cx="18" cy="18" r="3" /><path d="M18 15V11a4 4 0 0 0-4-4h-3" /><path d="M13 4l-2 3 2 3" /></> },
  stop: { fill: true, body: <rect x="6" y="6" width="12" height="12" rx="2.5" /> },
  edit: { sw: 1.7, body: <><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z" /></> },
  list: { sw: 1.8, body: <><line x1="8" y1="6" x2="20" y2="6" /><line x1="8" y1="12" x2="20" y2="12" /><line x1="8" y1="18" x2="20" y2="18" /><circle cx="4" cy="6" r="1" /><circle cx="4" cy="12" r="1" /><circle cx="4" cy="18" r="1" /></> },
  cpu: { sw: 1.7, body: <><rect x="6" y="6" width="12" height="12" rx="2" /><path d="M9 2v2M15 2v2M9 20v2M15 20v2M2 9h2M2 15h2M20 9h2M20 15h2" /></> },
  download: { sw: 1.8, body: <><path d="M12 3v12" /><path d="M7 11l5 5 5-5" /><path d="M5 21h14" /></> },
  square: { sw: 1.7, body: <rect x="4" y="4" width="16" height="16" rx="3" /> },
  "square-check": { sw: 1.9, body: <><rect x="4" y="4" width="16" height="16" rx="3" /><path d="M8.5 12.2l2.4 2.4 4.6-4.9" /></> },
  "square-x": { sw: 1.9, body: <><rect x="4" y="4" width="16" height="16" rx="3" /><path d="M9.2 9.2l5.6 5.6M14.8 9.2l-5.6 5.6" /></> },
};

function Sym({ n, s = 14, cls }: { n: SymName; s?: number; cls?: string }) {
  const ic = ICONS[n];
  return (
    <svg width={s} height={s} viewBox="0 0 24 24" className={cls} fill={ic.fill ? "currentColor" : "none"} stroke={ic.fill ? "none" : "currentColor"} strokeWidth={ic.sw} strokeLinecap="round" strokeLinejoin="round" style={{ display: "block", flex: "none" }} aria-hidden="true">
      {ic.body}
    </svg>
  );
}