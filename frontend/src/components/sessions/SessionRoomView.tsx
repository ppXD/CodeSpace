import { createContext, Fragment, type ReactNode, useContext, useEffect, useState } from "react";
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
  LiveActivityBlock,
  NarrativeStepBlock,
  PlanChecklistBlock,
  PlanChecklistItem,
  RoomAction,
  RoomAgentCard,
  RoomBlock,
  RoomFilePreview,
  RoomPlanQuestion,
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
import { planAgentStatus, planDepsLabel, planStateIcon, planStateTone, planStateWord, composePlanFeedback } from "@/lib/planChecklist";
import { useConfirm } from "@/components/dialog";
import { LaunchTaskModal } from "@/components/tasks/LaunchTaskModal";
import { isRunActive, useCancelRun, usePendingDecisions, useReplayRun } from "@/hooks/use-workflows";

/** What the right-side preview drawer is showing — an agent (its terminal) or a file (its content + download). */
type DrawerTarget =
  | { kind: "agent"; agent: RoomAgentCard; runId: string }
  | { kind: "file"; runId: string; path: string; agentRunId?: string };

/** Open the unified preview drawer. Any row (an agent card, a changed file) calls this to preview on the right. */
const RoomDrawerContext = createContext<(t: DrawerTarget) => void>(() => {});
const useRoomDrawer = () => useContext(RoomDrawerContext);

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
export function SessionRoomView({ teamSlug, room, onOpenRoom }: { teamSlug: string; room: RoomView; onOpenRoom: (runId?: string) => void }) {
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

  const [drawer, setDrawer] = useState<DrawerTarget | null>(null);

  return (
    <RoomDrawerContext.Provider value={setDrawer}>
    <section className="room-room" data-drawer={drawer ? true : undefined}>
      <header className="room-head">
        <div className="room-head-top">
          <nav className="room-crumbs">
            <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Sessions</a>
            <span className="room-crumb-sep">/</span>
            <span className="room-crumb-cur">{room.sessionId.slice(0, 6)}</span>
          </nav>
          <div className="room-head-icons">
            <button className="room-icon-btn" title="Run details — the raw graph, trace, decisions" onClick={() => onOpenRoom()}><Sym n="terminal" s={16} /></button>
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

      {drawer && <RoomDrawer target={drawer} onClose={() => setDrawer(null)} />}
    </section>
    </RoomDrawerContext.Provider>
  );
}

/** The right-side preview drawer — a panel scoped inside the room, no dimming scrim (the main conversation stays live + full-colour). */
function RoomDrawer({ target, onClose }: { target: DrawerTarget; onClose: () => void }) {
  return (
    <aside className="room-drawer">
      {target.kind === "agent"
        ? <AgentDrawer agent={target.agent} runId={target.runId} onClose={onClose} />
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
function TopBlock({ block, anchorId, nowMs, onOpenRun, onOpenRoom }: { block: RoomBlock; anchorId?: string | null; nowMs: number; onOpenRun: (runId: string) => void; onOpenRoom: (runId?: string) => void }) {
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
function AssistantTurn({ turn, anchored, nowMs, onOpenRun, onOpenRoom }: { turn: AssistantTurnBlock; anchored: boolean; nowMs: number; onOpenRun: (runId: string) => void; onOpenRoom: (runId?: string) => void }) {
  const live = isRunActive(turn.status);
  const [open, setOpen] = useState(anchored || live);

  const tone = statusTone(turn.status, live);

  const hasDecision = turn.blocks.some((b) => b.type === "decision");
  const decisions = usePendingDecisions(hasDecision && live);
  const agentIds = new Set(turn.blocks.flatMap((b) => (b.type === "agent_group" ? b.agents.map((a) => a.agentRunId) : [])));
  const liveDecisions = decisions.data ? decisionsForRun(decisions.data, turn.runId, agentIds) : [];
  const pdById = new Map(liveDecisions.map((d) => [d.id, d]));

  // The final answer belongs at the END, in the RESULT card — not echoed at the top. When the turn carries a
  // final_answer block (its summary IS that answer), drop the opening lead so the answer shows once, at the bottom.
  // A live / failed turn has no RESULT card, so its lead still surfaces the one-line status.
  const hasFinalAnswer = turn.blocks.some((b) => b.type === "final_answer");
  const lead = turn.summary && !GENERIC_SUMMARY.has(turn.summary) && !hasFinalAnswer ? turn.summary : null;

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
function InnerBlock({ block, pdById, onOpenRoom }: { block: RoomBlock; pdById: Map<string, PendingDecision>; onOpenRoom: (runId?: string) => void }) {
  if (block.type === "stat") return <StatRow stat={block as StatBlock} />;
  if (block.type === "plan_checklist") return <PlanChecklistCard plan={block as PlanChecklistBlock} />;
  if (block.type === "agent_group") return <AgentSection group={block as AgentGroupBlock} />;
  if (block.type === "narrative_step") return <SupervisorStep step={block as NarrativeStepBlock} />;
  if (block.type === "delivery") return <PrCard delivery={block as DeliveryBlock} />;
  if (block.type === "final_answer") return <FinalAnswer answer={block as FinalAnswerBlock} />;
  if (block.type === "live_activity") return <LiveTicker live={block as LiveActivityBlock} />;
  if (block.type === "diagnostic") return <ErrorCard diag={block as DiagnosticBlock} onOpenRoom={onOpenRoom} />;
  if (block.type === "decision") {
    const d = block as DecisionBlock;
    const liveD = pdById.get(d.decisionId);
    return liveD ? <div className="room-decision"><DecisionCard decision={liveD} /></div> : <DecisionPreview decision={d} />;
  }
  return <p className="room-para room-muted">{describeUnknown(block)}</p>;
}

/** A translated supervisor operation between rounds — a sparkle chip + the one-liner ("Merging results", "Deciding: X"). */
function SupervisorStep({ step }: { step: NarrativeStepBlock }) {
  const tone = step.tone === "Error" ? "err" : step.tone === "Success" ? "ok" : "info";
  return (
    <div className={`room-supstep room-supstep-${tone}`}>
      <Sym n="sparkle" s={13} cls="room-supstep-ic" />
      <span className="room-supstep-text"><Inline text={step.text} /></span>
    </div>
  );
}

/** The rich final answer — the closing text, then attachments grouped by kind: inline images, file links (preview / download), and the PR. */
function FinalAnswer({ answer }: { answer: FinalAnswerBlock }) {
  const openDrawer = useRoomDrawer();
  const run = useContext(RunActionsContext);
  const atts = answer.attachments ?? [];
  const images = atts.filter((a) => a.kind === "Image");
  const files = atts.filter((a) => a.kind === "FileLink");
  const prs = atts.filter((a) => a.kind === "Pr");

  return (
    <div className="room-final">
      <div className="room-final-head"><Sym n="check" s={13} cls="room-final-ic" /> Result</div>
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

/** The live "working…" indicator pinned at the bottom of an active turn — a pulsing dot + the streaming activity line. */
function LiveTicker({ live }: { live: LiveActivityBlock }) {
  return (
    <div className="room-live">
      <span className="room-live-dot" />
      <span className="room-live-text">{live.text}</span>
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
      {item.detail && <span className="room-file-stat"><DiffStat text={item.detail} /></span>}
      <Sym n="chevron-right" s={11} cls="room-file-caret" />
    </button>
  );
}

/** The run's plan as a live checklist — the whole current version, one checkable row per item: state icon ·
 *  title · contract chips (kind / dependencies / acceptance / criteria / attempts) · state word · Details. The
 *  backend owns every string; unknown states render neutral. Questions/assumptions are read-only here. */
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
      </div>
      <span className={`room-prow-state room-prow-state-${tone}`}>{planStateWord(it.state)}</span>
      <button
        className="room-prow-act"
        disabled={!canOpen}
        onClick={() => canOpen && openDrawer({ kind: "agent", runId: run!.runId, agent: { agentRunId: it.agentRunId!, label: it.title, status: planAgentStatus(it.state) } })}
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
  const toolCount = a.toolCount ?? 0;

  return (
    <div className="room-arow-wrap">
      <button className="room-arow" data-queued={queued || undefined} disabled={!run} onClick={() => run && openDrawer({ kind: "agent", agent: a, runId: run.runId })}>
        <span className={`room-adot room-adot-${cls}`} />
        <span className="room-arow-name" title={a.summary ?? a.label}>{a.label}</span>
        {(toolCount > 0 || fileCount > 0) && (
          <span className="room-arow-meta">
            {toolCount > 0 && <span className="room-arow-metaitem" title={`${toolCount} tool call${toolCount === 1 ? "" : "s"}`}><Sym n="terminal" s={10} cls="room-arow-metaic" /> {toolCount} {toolCount === 1 ? "tool" : "tools"}</span>}
            {fileCount > 0 && <span className="room-arow-metaitem" title={`${fileCount} file${fileCount === 1 ? "" : "s"} changed`}><Sym n="file" s={10} cls="room-arow-metaic" /> {fileCount} {fileCount === 1 ? "file" : "files"}</span>}
          </span>
        )}
        <span className="room-arow-time">{a.durationMs != null ? formatDurationMs(a.durationMs) : "—"}</span>
        <span className={`room-arow-state room-arow-state-${cls}`}>{agentStatusWord(a.status)}</span>
        <span className="room-arow-act">{action} <Sym n="chevron-right" s={11} /></span>
      </button>
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
function ErrorCard({ diag, onOpenRoom }: { diag: DiagnosticBlock; onOpenRoom: (runId?: string) => void }) {
  const [showRaw, setShowRaw] = useState(false);
  const run = useContext(RunActionsContext);
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
                onClick={act.kind === "OpenTrace" ? () => onOpenRoom(run?.runId) : undefined}
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
function TurnActions({ actions, turn, onOpenRoom, onOpenRun }: { actions: RoomAction[]; turn: AssistantTurnBlock; onOpenRoom: (runId?: string) => void; onOpenRun: (runId: string) => void }) {
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

  // The doing-actions render first; "View trace" is always last (a quiet ghost). Stop / Rerun show ONLY when the
  // capability is enabled — a finished turn shows Rerun (no Stop); a running turn shows Stop (no Rerun).
  const trace = actions.find((a) => a.kind === "OpenTrace");
  const doing = actions.filter((a) => a.kind !== "OpenTrace" && a.enabled);

  return (
    <div className="room-foot">
      {doing.map((a) => {
        if (a.kind === "Stop") return <button key={a.kind} className="room-btn-stop" onClick={() => void onStop()} disabled={cancel.isPending}><i className="room-stop-pulse" /> {cancel.isPending ? "Stopping…" : "Stop"}</button>;
        if (a.kind === "RerunTurn") return <button key={a.kind} className="room-btn" onClick={() => void onRerun()} disabled={replay.isPending}><Sym n="rerun" s={13} /> {replay.isPending ? "Rerunning…" : a.label}</button>;
        if (a.kind === "RerunFromNode") return <button key={a.kind} className="room-btn" title={a.disabledReason ?? undefined}><Sym n="branch" s={13} /> {a.label}</button>;
        return null;
      })}
      {trace && <button className="room-btn-ghost" onClick={() => onOpenRoom(turn.runId)}><Sym n="terminal" s={13} /> {trace.label}</button>}
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
  | "pr" | "stop" | "edit" | "list" | "cpu" | "download" | "square" | "square-check" | "square-x";

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
