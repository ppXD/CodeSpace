import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { AgentEditorModal } from "@/components/agents/AgentEditor";
import { AgentRosterRow } from "@/components/agents/AgentRosterRow";
import { ImportPackModal } from "@/components/agents/ImportPackModal";
import { NewAgentModal } from "@/components/agents/NewAgentModal";
import { filterAgents, type OriginFilter } from "@/components/agents/agentFilter";
import { LaunchTaskModal } from "@/components/tasks/LaunchTaskModal";
import { useAgentDefinitions, useAgentStats } from "@/hooks/use-agents";

type EditorState = { mode: "create" } | { mode: "edit"; id: string } | null;

/** The stats time-window: a trend horizon fed to the backend's `since` filter. "All" sends no window. */
const WINDOWS: { v: string; l: string; days: number | null }[] = [
  { v: "7", l: "Last 7 days", days: 7 },
  { v: "30", l: "Last 30 days", days: 30 },
  { v: "all", l: "All time", days: null },
];

/**
 * Agents — the team's reusable personas as a working roster. Each agent is a row that reads left-to-right: identity,
 * its loadout (model / autonomy / tools / skills), then its recent-run evidence (an outcome sparkline, windowed
 * success rate, latency, spend, last-active) joined from the per-agent stats endpoint. The primary action is
 * "Launch task", which opens the ONE generic composer with this persona injected. Measurement is per-agent and
 * on-row now — the old global Fleet-health card is gone (the fleet compare moves to the Runs analytics surface).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/agents/")({
  component: AgentsListPage,
});

function AgentsListPage() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();

  const agents = useAgentDefinitions();
  const rows = agents.data ?? [];

  const [query, setQuery] = useState("");
  const [origin, setOrigin] = useState<OriginFilter>("all");
  const [windowSel, setWindowSel] = useState("7");
  const [editor, setEditor] = useState<EditorState>(null);
  const [importing, setImporting] = useState(false);
  const [choosing, setChoosing] = useState(false);
  const [launchAgentId, setLaunchAgentId] = useState<string | null>(null);

  const windowed = windowSel !== "all";
  // Quantize "now" to the hour so the react-query key is STABLE across a session (a raw ms-precision Date.now()
  // would mint a fresh key every render/mount and defeat the staleTime cache). Captured ONCE in a lazy state
  // initializer — render stays pure (react-hooks/purity), and the hour-grain horizon never moves mid-session.
  const [nowHour] = useState(() => {
    const hourMs = 3_600_000;
    return Math.floor(Date.now() / hourMs) * hourMs;
  });
  const since = useMemo(() => {
    const days = WINDOWS.find((w) => w.v === windowSel)?.days;
    if (days == null) return undefined;
    return new Date(nowHour - days * 86_400_000).toISOString();
  }, [windowSel, nowHour]);

  const stats = useAgentStats(since);
  const statById = useMemo(
    () => new Map((stats.data?.agents ?? []).map((s) => [s.agentDefinitionId, s])),
    [stats.data],
  );

  const importedCount = rows.filter((a) => a.origin === "Imported").length;
  const authoredCount = rows.length - importedCount;
  const visible = filterAgents(rows, query, origin);

  const hasAgents = !agents.isLoading && !agents.error && rows.length > 0;

  const openNew = () => setChoosing(true);
  const viewRuns = (agentId: string) => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug }, search: { agentDefinitionIds: [agentId] } });

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs">
          <span className="cur">Agents</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Agents</h1>
          <div className="ct-actions">
            <button type="button" className="btn" onClick={() => setImporting(true)}><Ic.Download size={14} /> Import pack</button>
            <button type="button" className="btn btn-primary" onClick={openNew}><Ic.Plus size={14} /> New agent</button>
          </div>
        </div>
        {hasAgents && (
          <div className="ct-sub">
            {rows.length} {rows.length === 1 ? "agent" : "agents"}
            {importedCount > 0 && ` · ${importedCount} from packs`}
          </div>
        )}
      </div>

      <div className="ct-body">
        {agents.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {agents.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load agents</div>
            <div className="cn-banner-p">{agents.error.message}</div>
          </div>
        )}

        {!agents.isLoading && !agents.error && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No agents yet</div>
            <div className="ct-empty-p">Reusable units — a system prompt, model, skills, and tools a supervisor can <strong>@-mention</strong> and schedule — will appear here.</div>
            <div style={{ display: "flex", gap: 8, justifyContent: "center", marginTop: 14 }}>
              <button type="button" className="btn" onClick={() => setImporting(true)}><Ic.Download size={14} /> Import a pack</button>
              <button type="button" className="btn btn-primary" onClick={openNew}><Ic.Plus size={14} /> New agent</button>
            </div>
          </div>
        )}

        {hasAgents && (
          <>
            <div className="ar-toolbar">
              <div className="ct-search ar-search">
                <Ic.Search size={14} />
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Search agents…"
                  aria-label="Search agents"
                />
              </div>
              <div className="ar-filters">
                <div className="ct-tabs">
                  <SegTab value="all" current={origin} count={rows.length} onSelect={setOrigin}>All</SegTab>
                  <SegTab value="Authored" current={origin} count={authoredCount} onSelect={setOrigin}>Authored</SegTab>
                  <SegTab value="Imported" current={origin} count={importedCount} onSelect={setOrigin}>Imported</SegTab>
                </div>
                <div className="ct-tabs ar-window" role="group" aria-label="Stats window">
                  {WINDOWS.map((w) => <SegTab key={w.v} value={w.v} current={windowSel} onSelect={setWindowSel}>{w.l}</SegTab>)}
                </div>
              </div>
            </div>

            {visible.length === 0 ? (
              <div className="ct-empty">
                <div className="ct-empty-h">No matching agents</div>
                <div className="ct-empty-p">No agent matches the current search and filter.</div>
              </div>
            ) : (
              <div className="ar-list">
                {visible.map((a) => (
                  <AgentRosterRow
                    key={a.id}
                    agent={a}
                    stat={statById.get(a.id)}
                    statsPending={stats.isLoading}
                    statsError={stats.isError}
                    windowed={windowed}
                    onLaunch={() => setLaunchAgentId(a.id)}
                    onEdit={() => setEditor({ mode: "edit", id: a.id })}
                    onViewRuns={() => viewRuns(a.id)}
                  />
                ))}
              </div>
            )}
          </>
        )}
      </div>

      {editor && (
        <AgentEditorModal
          mode={editor.mode}
          agentId={editor.mode === "edit" ? editor.id : undefined}
          onClose={() => setEditor(null)}
        />
      )}

      {importing && <ImportPackModal onClose={() => setImporting(false)} />}

      {choosing && (
        <NewAgentModal
          onCustom={() => { setChoosing(false); setEditor({ mode: "create" }); }}
          onCreated={(id) => { setChoosing(false); setEditor({ mode: "edit", id }); }}
          onClose={() => setChoosing(false)}
        />
      )}

      {launchAgentId && (
        <LaunchTaskModal
          surface="chat"
          autofill={{ agentDefinitionId: launchAgentId }}
          onClose={() => setLaunchAgentId(null)}
          onLaunched={(runId) => { setLaunchAgentId(null); navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } }); }}
        />
      )}
    </section>
  );
}

/** One segmented filter toggle — the warm underline-tab look, keyboard-operable (role=button + Enter/Space). Generic
 *  over the value type, with an optional count pill, so it serves both the origin filter and the stats window. */
function SegTab<T extends string>({ value, current, count, onSelect, children }: { value: T; current: T; count?: number; onSelect: (v: T) => void; children: React.ReactNode }) {
  const active = current === value;

  return (
    <span
      className="ct-tab"
      role="button"
      tabIndex={0}
      aria-pressed={active}
      data-active={active ? "true" : undefined}
      onClick={() => onSelect(value)}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(value); } }}
    >
      {children}{count != null && <span className="ct-tab-c">{count}</span>}
    </span>
  );
}
