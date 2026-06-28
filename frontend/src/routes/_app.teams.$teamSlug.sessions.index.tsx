import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import type { SessionSummary } from "@/api/sessions";
import { RunStatusBadge } from "@/components/workflows/RunStatusBadge";
import { compactAge } from "@/components/workflows/cockpit";
import { useTeamSessions } from "@/hooks/use-sessions";

/**
 * The Sessions index — every work thread the team owns, most-recently-active first. Each row opens the thread as a
 * conversation. A session is one long-running work context; a run is one turn of it. Polled while open so live
 * threads surface their latest activity.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/sessions/")({
  component: TeamSessionsPage,
});

function TeamSessionsPage() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();

  // A slow clock so the relative ages stay fresh without churning every render.
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const sessions = useTeamSessions();
  const items = sessions.data?.pages.flatMap((p) => p.items) ?? [];

  const open = (sessionId: string) => navigate({ to: "/teams/$teamSlug/sessions/$sessionId", params: { teamSlug, sessionId } });

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Sessions</span></div>
        <div className="ct-title-row"><h1 className="ct-title">Sessions</h1></div>
      </div>

      <div className="ct-body">
        {sessions.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

        {!sessions.isLoading && sessions.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err">
            <div className="cn-banner-h">Couldn't load sessions</div>
            <div className="cn-banner-p">{sessions.error.message}</div>
          </div>
        )}

        {!sessions.isLoading && !sessions.error && items.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No sessions yet</div>
            <div className="ct-empty-p">Launch a task and it becomes a session — a thread you can read back and continue.</div>
          </div>
        )}

        {items.length > 0 && (
          <div style={{ display: "flex", flexDirection: "column", gap: 8, maxWidth: 880 }}>
            {items.map((s) => <SessionRow key={s.id} s={s} nowMs={nowMs} onOpen={() => open(s.id)} />)}

            {sessions.hasNextPage && (
              <button
                className="btn btn-ghost"
                style={{ alignSelf: "center", marginTop: 8 }}
                disabled={sessions.isFetchingNextPage}
                onClick={() => void sessions.fetchNextPage()}
              >
                {sessions.isFetchingNextPage ? "Loading…" : "Load more"}
              </button>
            )}
          </div>
        )}
      </div>
    </section>
  );
}

function SessionRow({ s, nowMs, onOpen }: { s: SessionSummary; nowMs: number; onOpen: () => void }) {
  return (
    <div
      onClick={onOpen}
      title={s.title}
      style={{
        cursor: "pointer", display: "flex", alignItems: "center", gap: 12, padding: "12px 14px",
        background: "var(--panel)", border: "1px solid var(--line)", borderRadius: "var(--radius)",
      }}
    >
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, minWidth: 0 }}>
          <span style={{ fontWeight: 500, color: "var(--ink)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{s.title}</span>
          {s.hasPendingDecision && <span className="wf-status-pill wf-status-suspended" style={{ flex: "none" }}>Needs you</span>}
        </div>
        <div style={{ marginTop: 4, fontSize: 12, color: "var(--muted)" }}>
          {s.kind} · {s.turnCount} turn{s.turnCount === 1 ? "" : "s"} · {compactAge(s.lastActivityAt, nowMs)}
        </div>
      </div>

      {s.latestRunStatus && <RunStatusBadge status={s.latestRunStatus} />}
    </div>
  );
}
