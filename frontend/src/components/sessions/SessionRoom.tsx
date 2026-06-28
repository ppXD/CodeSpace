import { useEffect, useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { SessionDetail, SessionTurn } from "@/api/sessions";
import { tasksApi } from "@/api/tasks";
import { compactAge } from "@/components/workflows/cockpit";
import { RunCanvas } from "@/components/workflows/RunCanvas";
import { RunStatusBadge } from "@/components/workflows/RunStatusBadge";
import { isRunActive, useNodeManifests, useWorkflowRun } from "@/hooks/use-workflows";

/**
 * The Session room — the run-detail experience for a run that belongs to a work session. Renders the session as a
 * conversation of turns (each turn = a run: the user's message + the run's outcome), where each turn's box embeds the
 * live RunCanvas (real-time progress while running, the final graph once done). Entered from the Runs list → a run →
 * here, anchored at that run's turn. A composer at the bottom continues the thread as the next turn.
 */
export function SessionRoom({ teamSlug, session, anchorRunId, onOpenRoom }: { teamSlug: string; session: SessionDetail; anchorRunId: string; onOpenRoom: () => void }) {
  const navigate = useNavigate();
  const qc = useQueryClient();

  const manifests = useNodeManifests();
  const manifestByType = useMemo(() => new Map((manifests.data ?? []).map((m) => [m.typeKey, m])), [manifests.data]);

  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  const openRun = (runId: string) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } });

  const send = async () => {
    const text = draft.trim();
    if (!text || sending) return;

    setSending(true);
    setSendError(null);
    try {
      await tasksApi.launch({ taskText: text, surfaceKind: "chat", sessionId: session.id, effort: "quick" });
      setDraft("");
      // The continue starts the next turn — refetch the thread (the by-run resolver feeds this view) so it appears.
      await qc.invalidateQueries({ queryKey: ["run-session", anchorRunId] });
      await qc.invalidateQueries({ queryKey: ["session", session.id] });
    } catch (e) {
      setSendError(e instanceof ApiError ? e.message : "Couldn't continue the session.");
    } finally {
      setSending(false);
    }
  };

  return (
    <section className="ct" style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <div className="ct-head" style={{ paddingBottom: 14 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Runs</a>
          <span className="sep">/</span>
          <span className="cur">{session.title}</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">{session.title}</h1>
            <div style={{ fontSize: 12, color: "var(--muted)", marginTop: 2 }}>
              {session.kind} · {session.status} · {session.turns.length} turn{session.turns.length === 1 ? "" : "s"}
            </div>
          </div>
          <div className="ct-actions">
            <button className="btn" onClick={onOpenRoom} title="Open the classic Run Room (activity, terminals, trace, decisions) for the anchored run.">
              <Ic.Workflow size={13} /> Run Room
            </button>
          </div>
        </div>
      </div>

      <div className="ct-body" style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
        <div style={{ maxWidth: 920, margin: "0 auto", display: "flex", flexDirection: "column", gap: 20 }}>
          {session.summary && (
            <div style={{ padding: "10px 14px", background: "var(--panel-2)", border: "1px solid var(--line)", borderRadius: "var(--radius)", fontSize: 13, color: "var(--muted)" }}>
              <div style={{ fontWeight: 500, color: "var(--ink-2)", marginBottom: 4 }}>Earlier work (summary)</div>
              <div style={{ whiteSpace: "pre-wrap" }}>{session.summary}</div>
            </div>
          )}

          {session.turns.map((t) => (
            <TurnCard
              key={t.turnIndex}
              turn={t}
              manifestByType={manifestByType}
              anchored={session.anchorTurnIndex === t.turnIndex}
              nowMs={nowMs}
              onOpenRun={openRun}
            />
          ))}

          {session.turns.length === 0 && <div className="ct-empty"><div className="ct-empty-h">No turns yet</div></div>}
        </div>
      </div>

      <div style={{ borderTop: "1px solid var(--line)", padding: "12px 16px", background: "var(--panel)" }}>
        <div style={{ maxWidth: 920, margin: "0 auto", display: "flex", gap: 8, alignItems: "flex-end" }}>
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); void send(); } }}
            placeholder={session.status === "Archived" ? "This session is archived." : "Continue this session — describe the next turn…"}
            rows={2}
            disabled={sending || session.status === "Archived"}
            style={{ flex: 1, resize: "none", padding: "8px 10px", background: "var(--bg)", border: "1px solid var(--line-2)", borderRadius: "var(--radius)", color: "var(--ink)", font: "inherit" }}
          />
          <button className="btn btn-primary" disabled={sending || !draft.trim() || session.status === "Archived"} onClick={() => void send()}>
            {sending ? "Sending…" : "Send"}
          </button>
        </div>
        {sendError && <div style={{ maxWidth: 920, margin: "6px auto 0", color: "var(--danger)", fontSize: 12 }}>{sendError}</div>}
      </div>
    </section>
  );
}

function TurnCard({ turn, manifestByType, anchored, nowMs, onOpenRun }: { turn: SessionTurn; manifestByType: Map<string, import("@/api/workflows").NodeManifestDto>; anchored: boolean; nowMs: number; onOpenRun: (runId: string) => void }) {
  // The canvas (a React Flow instance) is rendered for the anchored + still-running turns by default; older finished
  // turns collapse to a one-line header you can expand — keeps a long thread from mounting many graphs at once.
  const [open, setOpen] = useState(anchored || isRunActive(turn.runStatus));

  return (
    <div style={anchored ? { outline: "2px solid var(--accent)", borderRadius: "var(--radius-lg)", padding: 10, margin: -10 } : undefined}>
      {turn.userMessage && (
        <div style={{ alignSelf: "flex-end", marginLeft: "auto", maxWidth: "80%", padding: "10px 14px", background: "var(--accent-soft)", color: "var(--ink)", borderRadius: "var(--radius-lg)", whiteSpace: "pre-wrap", marginBottom: 10 }}>
          {turn.userMessage}
        </div>
      )}

      <div style={{ border: "1px solid var(--line)", borderRadius: "var(--radius-lg)", background: "var(--panel)", overflow: "hidden" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "10px 14px", flexWrap: "wrap", borderBottom: open ? "1px solid var(--line)" : "none" }}>
          <span style={{ fontSize: 12, color: "var(--muted)" }}>Turn {turn.turnIndex}</span>
          <RunStatusBadge status={turn.runStatus} />
          {turn.projectionKind && <span style={{ fontSize: 12, color: "var(--muted-2)" }}>{turn.projectionKind}</span>}
          {turn.hasPendingDecision && <span className="wf-status-pill wf-status-suspended">Needs you</span>}
          {turn.attemptCount > 1 && <span style={{ fontSize: 12, color: "var(--muted)" }}>· {turn.attemptCount} attempts</span>}
          <span style={{ flex: 1 }} />
          <span style={{ fontSize: 12, color: "var(--muted-2)" }}>{compactAge(turn.createdDate, nowMs)}</span>
          <button className="btn btn-ghost" style={{ padding: "2px 8px", fontSize: 12 }} onClick={() => setOpen((o) => !o)}>
            {open ? "Hide canvas" : "Show canvas"}
          </button>
          <button className="btn btn-ghost" style={{ padding: "2px 8px", fontSize: 12 }} onClick={() => onOpenRun(turn.runId)}>Open full detail →</button>
        </div>

        {open && <TurnCanvas runId={turn.runId} manifestByType={manifestByType} onOpenRun={onOpenRun} />}

        {(turn.result || turn.error || turn.producedBranch) && (
          <div style={{ padding: "10px 14px", borderTop: open ? "1px solid var(--line)" : "none" }}>
            {turn.result && <div style={{ color: "var(--ink-2)", fontSize: 14, whiteSpace: "pre-wrap" }}>{turn.result}</div>}
            {turn.error && <div style={{ color: "var(--danger)", fontSize: 13, marginTop: turn.result ? 6 : 0, whiteSpace: "pre-wrap" }}>{turn.error}</div>}
            {turn.producedBranch && <div style={{ marginTop: 8, fontSize: 12, color: "var(--muted)" }}>branch <code>{turn.producedBranch}</code></div>}
          </div>
        )}
      </div>
    </div>
  );
}

function TurnCanvas({ runId, manifestByType, onOpenRun }: { runId: string; manifestByType: Map<string, import("@/api/workflows").NodeManifestDto>; onOpenRun: (runId: string) => void }) {
  const run = useWorkflowRun(runId);
  const r = run.data;

  if (run.isLoading) {
    return <div style={{ height: 360, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--muted)", fontSize: 13 }}>Loading canvas…</div>;
  }
  if (!r?.definition) {
    return <div style={{ height: 120, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--muted)", fontSize: 13 }}>This run's graph snapshot isn't available.</div>;
  }

  return (
    <div style={{ height: 360, width: "100%" }}>
      <RunCanvas definition={r.definition} runNodes={r.nodes} runStatus={r.status} manifestByType={manifestByType} runId={runId} onOpenRun={onOpenRun} />
    </div>
  );
}
