import { Fragment, type ReactNode, useEffect, useState } from "react";
import { useNavigate } from "@tanstack/react-router";

import type {
  AgentGroupBlock,
  AssistantTurnBlock,
  DecisionBlock,
  DeliveryBlock,
  DiagnosticBlock,
  ExecutionMapStep,
  ExecutionStepStatus,
  RoomAction,
  RoomAgentCard,
  RoomBlock,
  RoomView,
  StatBlock,
  StatItem,
} from "@/api/sessions";
import { sessionsApi } from "@/api/sessions";
import type { PendingDecision, PhaseAgentRef, WorkflowRunStatus } from "@/api/workflows";
import { AgentTerminal } from "@/components/workflows/AgentTerminal";
import { DecisionCard } from "@/components/workflows/DecisionCard";
import { RunActionsContext } from "@/components/workflows/runActionsContext";
import { RunOpenContext } from "@/components/workflows/runOpenContext";
import { decisionsForRun } from "@/components/workflows/runDecisions";
import { compactAge } from "@/components/workflows/cockpit";
import { useConfirm } from "@/components/dialog";
import { LaunchTaskModal } from "@/components/tasks/LaunchTaskModal";
import { isRunActive, useCancelRun, usePendingDecisions, useReplayRun } from "@/hooks/use-workflows";

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
export function SessionRoomView({ teamSlug, room, onOpenRoom }: { teamSlug: string; room: RoomView; onOpenRoom: () => void }) {
  const navigate = useNavigate();

  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 5_000);
    return () => clearInterval(t);
  }, []);

  const openRun = (runId: string) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } });

  const turnCount = room.blocks.filter((b) => b.type === "assistant_turn").length;
  const startedAt = room.blocks.map((b) => ("at" in b ? b.at : null)).find(Boolean) as string | undefined;

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

  return (
    <section className="room-room">
      <header className="room-head">
        <div className="room-head-top">
          <nav className="room-crumbs">
            <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Sessions</a>
            <span className="room-crumb-sep">/</span>
            <span className="room-crumb-cur">{room.sessionId.slice(0, 6)}</span>
          </nav>
          <div className="room-head-icons">
            <button className="room-icon-btn" title="Run details — the raw graph, trace, decisions" onClick={onOpenRoom}><Sym n="terminal" s={16} /></button>
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
          <span className={`room-status-pill room-status-${room.status === "Open" ? "open" : "closed"}`}>
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

      <div className="room-scroll">
        <div className="room-thread">
          {room.blocks.map((b) => (
            <TopBlock key={b.id} block={b} anchorId={room.anchorBlockId} nowMs={nowMs} onOpenRun={openRun} onOpenRoom={onOpenRoom} />
          ))}
          {turnCount === 0 && <div className="room-empty">No turns yet.</div>}
        </div>
      </div>

      <div className="room-composer">
        <div className="room-composer-inner">
          <LaunchTaskModal inline surface="chat" sessionId={room.sessionId} placeholder="Reply to continue this session…" onClose={() => {}} onLaunched={openRun} />
        </div>
      </div>
    </section>
  );
}

/** A top-level block: the user's message bubble, an assistant turn, or a forward-compat fallback. */
function TopBlock({ block, anchorId, nowMs, onOpenRun, onOpenRoom }: { block: RoomBlock; anchorId?: string | null; nowMs: number; onOpenRun: (runId: string) => void; onOpenRoom: () => void }) {
  if (block.type === "user_message") return <UserBubble text={block.text} at={block.at} nowMs={nowMs} />;
  if (block.type === "assistant_turn") return <AssistantTurn turn={block} anchored={anchorId === block.id} nowMs={nowMs} onOpenRun={onOpenRun} onOpenRoom={onOpenRoom} />;
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
function AssistantTurn({ turn, anchored, nowMs, onOpenRun, onOpenRoom }: { turn: AssistantTurnBlock; anchored: boolean; nowMs: number; onOpenRun: (runId: string) => void; onOpenRoom: () => void }) {
  const live = isRunActive(turn.status);
  const [open, setOpen] = useState(anchored || live);

  const tone = statusTone(turn.status, live);

  const hasDecision = turn.blocks.some((b) => b.type === "decision");
  const decisions = usePendingDecisions(hasDecision && live);
  const agentIds = new Set(turn.blocks.flatMap((b) => (b.type === "agent_group" ? b.agents.map((a) => a.agentRunId) : [])));
  const liveDecisions = decisions.data ? decisionsForRun(decisions.data, turn.runId, agentIds) : [];
  const pdById = new Map(liveDecisions.map((d) => [d.id, d]));

  const lead = turn.summary && !GENERIC_SUMMARY.has(turn.summary) ? turn.summary : null;

  return (
    <RunActionsContext.Provider value={{ runId: turn.runId, isTerminal: !live }}>
      <RunOpenContext.Provider value={onOpenRun}>
        <div className="room-turn">
          <div className="room-turn-head">
            <span className="room-av"><Sym n="terminal" s={13} /></span>
            <span className="room-av-name">CodeSpace</span>
            <span className={`room-pill room-pill-${tone}`}>
              {tone === "run" ? <i className="room-pill-dot" /> : <Sym n={pillIcon(tone)} s={11} />}
              {pillLabel(turn.status, live)}
            </span>
            <span className="room-turn-meta">Turn {turn.turnIndex}{turnMeta(turn, nowMs, live)}</span>
            <button className="room-collapse" title={open ? "Collapse turn" : "Expand turn"} onClick={() => setOpen((o) => !o)}>
              <Sym n={open ? "chevron-down" : "chevron-right"} s={14} />
            </button>
          </div>

          {open && (
            <div className="room-turn-body">
              {lead && <p className="room-lead"><Inline text={lead} /></p>}

              {turn.map && turn.map.steps.length > 0 && <RoomExecution steps={turn.map.steps} />}

              {turn.blocks.map((b) => <InnerBlock key={b.id} block={b} pdById={pdById} onOpenRoom={onOpenRoom} />)}

              <TurnActions actions={turn.actions} turn={turn} onOpenRoom={onOpenRoom} onOpenRun={onOpenRun} />
            </div>
          )}
        </div>
      </RunOpenContext.Provider>
    </RunActionsContext.Provider>
  );
}

/** The execution map — the design's bordered EXECUTION panel: backend-ordered stages as status circles with a label
 *  + per-stage detail, joined by connectors that read solid / dashed / animated by the surrounding stage states. */
function RoomExecution({ steps }: { steps: ExecutionMapStep[] }) {
  return (
    <div className="room-exec">
      <div className="room-exec-label">Execution</div>
      <div className="room-exec-flow">
        {steps.map((s, i) => {
          const idle = s.status === "Queued" || s.status === "Skipped" || s.status === "Pending";
          const deliver = s.label === "Deliver" && s.status === "Done";
          return (
            <Fragment key={s.id}>
              {i > 0 && <span className={`room-exec-conn room-exec-conn-${connKind(steps[i - 1].status, s.status)}`} aria-hidden="true" />}
              <div className="room-exec-node" data-idle={idle || undefined}>
                <span className={`room-exec-dot room-exec-dot-${execDotClass(s.status)}`} aria-hidden="true"><Sym n={execDotIcon(s.status, deliver)} s={s.status === "Running" ? 13 : idle ? 11 : 13} cls={s.status === "Running" ? "room-spin" : undefined} /></span>
                <div className="room-exec-text">
                  <span className="room-exec-name">{s.label}</span>
                  {s.detail && <span className={`room-exec-detail room-exec-detail-${execDotClass(s.status)}`}>{s.detail}</span>}
                </div>
              </div>
            </Fragment>
          );
        })}
      </div>
    </div>
  );
}

/** One inner block, rendered by type as a Codex-style detail row / card. */
function InnerBlock({ block, pdById, onOpenRoom }: { block: RoomBlock; pdById: Map<string, PendingDecision>; onOpenRoom: () => void }) {
  if (block.type === "stat") return <StatRow stat={block as StatBlock} />;
  if (block.type === "agent_group") return <AgentSection group={block as AgentGroupBlock} />;
  if (block.type === "delivery") return <PrCard delivery={block as DeliveryBlock} />;
  if (block.type === "diagnostic") return <ErrorCard diag={block as DiagnosticBlock} onOpenRoom={onOpenRoom} />;
  if (block.type === "decision") {
    const d = block as DecisionBlock;
    const liveD = pdById.get(d.decisionId);
    return liveD ? <div className="room-decision"><DecisionCard decision={liveD} /></div> : <DecisionPreview decision={d} />;
  }
  return <p className="room-para room-muted">{describeUnknown(block)}</p>;
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
  return (
    <div className="room-file">
      <span className="room-file-path">{item.text}</span>
      {item.detail && <span className="room-file-stat"><DiffStat text={item.detail} /></span>}
    </div>
  );
}

const AGENT_PIN_LIMIT = 6;

/** Agents — the design's compact "Work · N agents" panel: a counts header, then one row per agent (status dot · name ·
 *  time · state · quiet action). Failed / timed-out agents pin to the top; the rest collapse behind "Show N more". */
function AgentSection({ group }: { group: AgentGroupBlock }) {
  const [openId, setOpenId] = useState<string | null>(null);
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
        {shown.map((a) => (
          <AgentRow key={a.agentRunId} a={a} open={openId === a.agentRunId} onToggle={() => setOpenId((o) => (o === a.agentRunId ? null : a.agentRunId))} />
        ))}
      </div>
      {collapsible && (
        <button className="room-work-more" onClick={() => setExpanded((v) => !v)}>
          {expanded ? "Show less" : `Show ${hidden} more`} <Sym n={expanded ? "chevron-up" : "chevron-down"} s={13} />
        </button>
      )}
    </div>
  );
}

/** One agent as a compact row — status dot · name · time · state word · quiet action; expands to its terminal below. */
function AgentRow({ a, open, onToggle }: { a: RoomAgentCard; open: boolean; onToggle: () => void }) {
  const cls = agentTone(a.status);
  const running = a.status === "Running";
  const queued = a.status === "Queued" || a.status === "Pending";
  const action = running ? "Open terminal" : queued ? "View" : cls === "err" ? "View trace" : "Details";

  return (
    <div className="room-arow-wrap" data-open={open || undefined}>
      <button className="room-arow" data-queued={queued || undefined} onClick={onToggle} aria-expanded={open}>
        <span className={`room-adot room-adot-${cls}`} />
        <span className="room-arow-name" title={a.summary ?? a.label}>{a.label}</span>
        <span className="room-arow-time">{a.durationMs != null ? formatDurationMs(a.durationMs) : "—"}</span>
        <span className={`room-arow-state room-arow-state-${cls}`}>{agentStatusWord(a.status)}</span>
        <span className="room-arow-act">{action} <Sym n="chevron-right" s={11} /></span>
      </button>
      {open && <div className="room-agent-term"><AgentTerminal agent={toPhaseAgentRef(a)} onClose={onToggle} /></div>}
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
function ErrorCard({ diag, onOpenRoom }: { diag: DiagnosticBlock; onOpenRoom: () => void }) {
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
                disabled={!act.enabled}
                title={act.disabledReason ?? undefined}
                onClick={act.kind === "OpenTrace" ? onOpenRoom : undefined}
              >
                {act.kind === "FixCredentials" && <Sym n="lock" s={13} />}
                {act.kind === "RerunTurn" && <Sym n="rerun" s={13} />}
                {act.label}
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

/** The turn footer actions — the doing-actions first (Stop / Re-run / Retry), then "View trace" LAST. Re-run and Stop
 *  both confirm before firing; Stop is destructive-red with a live pulse. Capability-gated by the backend (never 422s). */
function TurnActions({ actions, turn, onOpenRoom, onOpenRun }: { actions: RoomAction[]; turn: AssistantTurnBlock; onOpenRoom: () => void; onOpenRun: (runId: string) => void }) {
  const replay = useReplayRun();
  const cancel = useCancelRun(turn.runId);
  const confirm = useConfirm();
  if (actions.length === 0) return null;

  const onRerun = async () => {
    const ok = await confirm({ title: "Re-run this turn?", message: "Starts a fresh attempt of this turn. The current result is kept in the turn's history.", confirmLabel: "Re-run", cancelLabel: "Cancel" });
    if (!ok) return;
    const result = await replay.mutateAsync(turn.runId);
    onOpenRun(result.runId);
  };

  const onStop = async () => {
    const ok = await confirm({ title: "Stop this run?", message: "Cancels the run and kills any in-flight agents. It can't be undone — you can Re-run a fresh copy.", confirmLabel: "Stop run", cancelLabel: "Keep running", destructive: true });
    if (ok) cancel.mutate();
  };

  // The doing-actions render first; "View trace" is always last (a quiet ghost). The backend already gates enablement.
  const trace = actions.find((a) => a.kind === "OpenTrace");
  const doing = actions.filter((a) => a.kind !== "OpenTrace");

  return (
    <div className="room-foot">
      {doing.map((a) => {
        if (a.kind === "Stop") return <button key={a.kind} className="room-btn-stop" onClick={() => void onStop()} disabled={cancel.isPending}><i className="room-stop-pulse" /><Sym n="stop" s={12} /> {cancel.isPending ? "Stopping…" : "Stop"}</button>;
        if (a.kind === "RerunTurn") return <button key={a.kind} className="room-btn" onClick={() => void onRerun()} disabled={!a.enabled || replay.isPending} title={a.disabledReason ?? undefined}><Sym n="rerun" s={13} /> {replay.isPending ? "Rerunning…" : a.label}</button>;
        if (a.kind === "RerunFromNode") return <button key={a.kind} className="room-btn" disabled={!a.enabled} title={a.disabledReason ?? undefined}><Sym n="branch" s={13} /> {a.label}</button>;
        return null;
      })}
      {trace && <button className="room-btn-ghost" onClick={onOpenRoom}><Sym n="terminal" s={13} /> {trace.label}</button>}
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
    agentRunId: c.agentRunId, nodeId: null, iterationKey: "", status: c.status, label: c.label, role: c.role ?? null,
    assignedSubtask: c.assignedSubtask ?? null, model: c.model ?? null, inputTokens: null, outputTokens: null,
    durationMs: c.durationMs ?? null, toolCount: c.toolCount ?? null, costUsd: c.costUsd ?? null, filesChanged: c.filesChanged ?? null,
  };
}

const FAILED_AGENT = new Set(["Failed", "Cancelled", "TimedOut"]);

function agentTone(status: string): string {
  if (FAILED_AGENT.has(status)) return "err";
  if (status === "Running") return "run";
  if (status === "Queued" || status === "Pending") return "idle";
  return "ok";
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
  if (status === "Success") return "Done";
  if (status === "Failure") return "Failed";
  if (status === "Cancelled") return "Stopped";
  if (status === "Suspended") return "Waiting";
  return live ? "Working" : status;
}

/** " · 2m 41s" (final duration), " · running 38s" (live), or " · 9h ago" (age fallback). */
function turnMeta(turn: AssistantTurnBlock, nowMs: number, live: boolean): string {
  if (turn.durationMs != null) return live ? ` · running ${formatDurationMs(turn.durationMs)}` : ` · ${formatDurationMs(turn.durationMs)}`;
  return turn.at ? ` · ${compactAge(turn.at, nowMs)}` : "";
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
  | "pr" | "stop" | "edit" | "list" | "cpu";

const ICONS: Record<SymName, { fill?: boolean; sw?: number; body: ReactNode }> = {
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
};

function Sym({ n, s = 14, cls }: { n: SymName; s?: number; cls?: string }) {
  const ic = ICONS[n];
  return (
    <svg width={s} height={s} viewBox="0 0 24 24" className={cls} fill={ic.fill ? "currentColor" : "none"} stroke={ic.fill ? "none" : "currentColor"} strokeWidth={ic.sw} strokeLinecap="round" strokeLinejoin="round" style={{ display: "block", flex: "none" }} aria-hidden="true">
      {ic.body}
    </svg>
  );
}
