import { useEffect, useState } from "react";
import { useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { SessionDetail, SessionTurn } from "@/api/sessions";
import { LaunchTaskModal } from "@/components/tasks/LaunchTaskModal";
import { compactAge } from "@/components/workflows/cockpit";
import { RunStatusBadge } from "@/components/workflows/RunStatusBadge";
import { isRunActive } from "@/hooks/use-workflows";

import { TurnTimeline } from "./TurnTimeline";

/**
 * The Session room — the run-detail experience for a run that belongs to a work session. Renders the session as a
 * conversation of turns: the user's message, then the run rendered AS AN AI REPLY by <see cref="TurnTimeline"/> (a
 * humanized header + a streaming step timeline with live agent tiles, decisions, and per-step reruns). Entered from the
 * Runs list → a run → here, anchored at that run's turn. The composer is the generic Launch Task composer, docked +
 * floating, continuing the thread as the next turn. The raw DAG stays one click away under "Run Room".
 */
export function SessionRoom({ teamSlug, session, onOpenRoom }: { teamSlug: string; session: SessionDetail; onOpenRoom: () => void }) {
  const navigate = useNavigate();

  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const openRun = (runId: string) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } });

  // The next turn defaults to the same effort tier the latest turn ran as; a continue navigates to the new run, which
  // re-resolves to this session anchored at the new turn.
  const latest = session.turns[session.turns.length - 1];
  const effort = inferEffort(latest?.projectionKind);

  return (
    <section className="ct" style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <div className="ct-head" style={{ paddingBottom: 14 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Runs</a>
          <span className="sep">/</span>
          <span className="cur">{session.title}</span>
        </div>
        <div className="ct-title-row">
          <div style={{ minWidth: 0 }}>
            <h1 className="ct-title" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{session.title}</h1>
            <div style={{ fontSize: 12.5, color: "var(--muted)", marginTop: 3 }}>
              {session.kind} · {session.status} · {session.turns.length} turn{session.turns.length === 1 ? "" : "s"}
            </div>
          </div>
          <div className="ct-actions">
            <button className="btn" onClick={onOpenRoom} title="Open the classic Run Room (the raw graph, trace, decisions) for the anchored run.">
              <Ic.Workflow size={13} /> Run Room
            </button>
          </div>
        </div>
      </div>

      <div className="ct-body" style={{ flex: 1, minHeight: 0, overflowY: "auto", paddingBottom: 4 }}>
        <div style={{ maxWidth: 760, margin: "0 auto", display: "flex", flexDirection: "column", gap: 30, paddingTop: 4 }}>
          {session.summary && (
            <div style={{ padding: "11px 15px", background: "var(--panel-2)", border: "1px solid var(--line)", borderRadius: "var(--radius-lg)", fontSize: 13, color: "var(--muted)" }}>
              <div style={{ fontWeight: 500, color: "var(--ink-2)", marginBottom: 4 }}>Earlier work · summary</div>
              <div style={{ whiteSpace: "pre-wrap", lineHeight: 1.6 }}>{session.summary}</div>
            </div>
          )}

          {session.turns.map((t) => (
            <TurnCard key={t.turnIndex} turn={t} anchored={session.anchorTurnIndex === t.turnIndex} nowMs={nowMs} onOpenRun={openRun} />
          ))}

          {session.turns.length === 0 && <div className="ct-empty"><div className="ct-empty-h">No turns yet</div></div>}
        </div>
      </div>

      <div className="session-composer">
        <LaunchTaskModal inline surface="chat" sessionId={session.id} autofill={{ effort }} onClose={() => {}} onLaunched={(runId) => openRun(runId)} />
      </div>
    </section>
  );
}

function inferEffort(projectionKind?: string | null): string {
  if (projectionKind === "supervisor") return "deep";
  if (projectionKind === "single-agent") return "quick";
  if (projectionKind && (projectionKind.startsWith("plan-map") || projectionKind === "coordinated-loop")) return "standard";
  return "auto";
}

function TurnCard({ turn, anchored, nowMs, onOpenRun }: { turn: SessionTurn; anchored: boolean; nowMs: number; onOpenRun: (runId: string) => void }) {
  // The step timeline mounts for the anchored + still-running turns by default; an older finished turn collapses to its
  // header + concluding summary, expandable on click — so a long thread never mounts many live timelines at once.
  const [open, setOpen] = useState(anchored || isRunActive(turn.runStatus));

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
      {turn.userMessage && (
        <div style={{ alignSelf: "flex-end", maxWidth: "82%", padding: "10px 14px", background: "var(--accent-soft)", color: "var(--ink)", borderRadius: 14, fontSize: 14, lineHeight: 1.55, whiteSpace: "pre-wrap" }}>
          {turn.userMessage}
        </div>
      )}

      <div style={{ borderRadius: "var(--radius-lg)", ...(anchored ? { background: "var(--panel-2)", padding: "2px 8px" } : {}) }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "4px 2px", flexWrap: "wrap" }}>
          <span style={{ fontSize: 11.5, fontWeight: 500, color: "var(--muted)", letterSpacing: ".02em" }}>TURN {turn.turnIndex}</span>
          <RunStatusBadge status={turn.runStatus} />
          {turn.hasPendingDecision && <span className="wf-status-pill wf-status-suspended">Needs you</span>}
          {turn.attemptCount > 1 && <span style={{ fontSize: 12, color: "var(--muted)" }}>· {turn.attemptCount} attempts</span>}
          <span style={{ flex: 1 }} />
          <span style={{ fontSize: 12, color: "var(--muted-2)" }}>{compactAge(turn.createdDate, nowMs)}</span>
          <button className="btn btn-ghost" style={{ padding: "3px 8px", fontSize: 12 }} onClick={() => setOpen((o) => !o)}>
            {open ? "Collapse" : "Expand"}
          </button>
          <button className="btn btn-ghost" style={{ padding: "3px 8px", fontSize: 12 }} onClick={() => onOpenRun(turn.runId)}>Open detail →</button>
        </div>

        {open && <TurnTimeline runId={turn.runId} onOpenRun={onOpenRun} />}

        {(turn.result || turn.producedBranch) && (
          <div style={{ padding: "8px 2px 4px" }}>
            {turn.result && <div style={{ color: "var(--ink-2)", fontSize: 14, lineHeight: 1.65, whiteSpace: "pre-wrap" }}>{turn.result}</div>}
            {turn.producedBranch && <div style={{ marginTop: 8, fontSize: 12, color: "var(--muted)" }}>branch <code>{turn.producedBranch}</code></div>}
          </div>
        )}
      </div>
    </div>
  );
}
