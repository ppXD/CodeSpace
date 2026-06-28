import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import type { SessionTurn } from "@/api/sessions";
import { tasksApi } from "@/api/tasks";
import { RunStatusBadge } from "@/components/workflows/RunStatusBadge";
import { compactAge } from "@/components/workflows/cockpit";
import { useSessionDetail } from "@/hooks/use-sessions";

/**
 * One work session as a conversation — the thread of turns (each turn = a run: the user's message + the run's
 * outcome), with a composer that continues the thread as the next turn. Live-polled while a turn is still running.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/sessions/$sessionId")({
  component: SessionDetailPage,
});

function SessionDetailPage() {
  const { teamSlug, sessionId } = Route.useParams();
  const navigate = useNavigate();
  const qc = useQueryClient();

  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const detail = useSessionDetail(sessionId);
  const d = detail.data;

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
      // Continue the thread as its next top-level turn (LaunchTaskCommand.SessionId → ContinueSessionId).
      await tasksApi.launch({ taskText: text, surfaceKind: "chat", sessionId, effort: "quick" });
      setDraft("");
      await qc.invalidateQueries({ queryKey: ["session", sessionId] });
    } catch (e) {
      setSendError(e instanceof ApiError ? e.message : "Couldn't continue the session.");
    } finally {
      setSending(false);
    }
  };

  if (detail.isLoading) {
    return <section className="ct"><div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></div></section>;
  }
  if (detail.error instanceof ApiError && detail.error.status === 404) {
    return <section className="ct"><div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Session not found</div><div className="ct-empty-p">It may have been removed, or it belongs to another team.</div></div></div></section>;
  }
  if (!d) {
    return <section className="ct"><div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Couldn't load this session</div></div></div></section>;
  }

  return (
    <section className="ct" style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <div className="ct-head" style={{ paddingBottom: 14 }}>
        <div className="ct-crumbs">
          <span style={{ cursor: "pointer", color: "var(--accent)" }} onClick={() => navigate({ to: "/teams/$teamSlug/sessions", params: { teamSlug } })}>Sessions</span>
          <span style={{ color: "var(--muted-2)", margin: "0 6px" }}>›</span>
          <span className="cur">{d.title}</span>
        </div>
        <div className="ct-title-row" style={{ alignItems: "center", gap: 10 }}>
          <h1 className="ct-title">{d.title}</h1>
          <span style={{ fontSize: 12, color: "var(--muted)" }}>{d.kind} · {d.status}</span>
        </div>
      </div>

      <div className="ct-body" style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
        <div style={{ maxWidth: 820, margin: "0 auto", display: "flex", flexDirection: "column", gap: 18 }}>
          {d.summary && (
            <div style={{ padding: "10px 14px", background: "var(--panel-2)", border: "1px solid var(--line)", borderRadius: "var(--radius)", fontSize: 13, color: "var(--muted)" }}>
              <div style={{ fontWeight: 500, color: "var(--ink-2)", marginBottom: 4 }}>Earlier work (summary)</div>
              <div style={{ whiteSpace: "pre-wrap" }}>{d.summary}</div>
            </div>
          )}

          {d.turns.map((t) => (
            <TurnBubble key={t.turnIndex} t={t} nowMs={nowMs} onOpenRun={openRun} anchored={d.anchorTurnIndex === t.turnIndex} />
          ))}

          {d.turns.length === 0 && <div className="ct-empty"><div className="ct-empty-h">No turns yet</div></div>}
        </div>
      </div>

      <div style={{ borderTop: "1px solid var(--line)", padding: "12px 16px", background: "var(--panel)" }}>
        <div style={{ maxWidth: 820, margin: "0 auto", display: "flex", gap: 8, alignItems: "flex-end" }}>
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); void send(); } }}
            placeholder={d.status === "Archived" ? "This session is archived." : "Continue this session — describe the next turn…"}
            rows={2}
            disabled={sending || d.status === "Archived"}
            style={{ flex: 1, resize: "none", padding: "8px 10px", background: "var(--bg)", border: "1px solid var(--line-2)", borderRadius: "var(--radius)", color: "var(--ink)", font: "inherit" }}
          />
          <button className="btn btn-primary" disabled={sending || !draft.trim() || d.status === "Archived"} onClick={() => void send()}>
            {sending ? "Sending…" : "Send"}
          </button>
        </div>
        {sendError && <div style={{ maxWidth: 820, margin: "6px auto 0", color: "var(--danger)", fontSize: 12 }}>{sendError}</div>}
      </div>
    </section>
  );
}

function TurnBubble({ t, nowMs, onOpenRun, anchored }: { t: SessionTurn; nowMs: number; onOpenRun: (runId: string) => void; anchored: boolean }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8, ...(anchored ? { outline: "2px solid var(--accent)", borderRadius: "var(--radius-lg)", padding: 10, margin: -10 } : {}) }}>
      {t.userMessage && (
        <div style={{ alignSelf: "flex-end", maxWidth: "85%", padding: "10px 14px", background: "var(--accent-soft)", color: "var(--ink)", borderRadius: "var(--radius-lg)", whiteSpace: "pre-wrap" }}>
          {t.userMessage}
        </div>
      )}

      <div style={{ alignSelf: "flex-start", width: "100%", padding: "12px 14px", background: "var(--panel)", border: "1px solid var(--line)", borderRadius: "var(--radius-lg)" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: t.result || t.error ? 8 : 0, flexWrap: "wrap" }}>
          <span style={{ fontSize: 12, color: "var(--muted)" }}>Turn {t.turnIndex}</span>
          <RunStatusBadge status={t.runStatus} />
          {t.projectionKind && <span style={{ fontSize: 12, color: "var(--muted-2)" }}>{t.projectionKind}</span>}
          {t.hasPendingDecision && <span className="wf-status-pill wf-status-suspended">Needs you</span>}
          {t.attemptCount > 1 && <span style={{ fontSize: 12, color: "var(--muted)" }}>· {t.attemptCount} attempts</span>}
          <span style={{ flex: 1 }} />
          <span style={{ fontSize: 12, color: "var(--muted-2)" }}>{compactAge(t.createdDate, nowMs)}</span>
        </div>

        {t.result && <div style={{ color: "var(--ink-2)", fontSize: 14, whiteSpace: "pre-wrap" }}>{t.result}</div>}
        {t.error && <div style={{ color: "var(--danger)", fontSize: 13, marginTop: 6, whiteSpace: "pre-wrap" }}>{t.error}</div>}

        {t.producedBranch && <div style={{ marginTop: 8, fontSize: 12, color: "var(--muted)" }}>branch <code>{t.producedBranch}</code></div>}
        {t.repositoryResults && t.repositoryResults.length > 0 && (
          <div style={{ marginTop: 8, fontSize: 12, color: "var(--muted)", display: "flex", flexDirection: "column", gap: 2 }}>
            {t.repositoryResults.map((r) => <div key={r.repositoryId}>branch <code>{r.producedBranch}</code></div>)}
          </div>
        )}

        <div style={{ marginTop: 10 }}>
          <button className="btn btn-ghost" onClick={() => onOpenRun(t.runId)}>Open in Run Room →</button>
        </div>
      </div>
    </div>
  );
}
