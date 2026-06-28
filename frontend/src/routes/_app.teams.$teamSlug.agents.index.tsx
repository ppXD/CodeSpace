import { createFileRoute } from "@tanstack/react-router";
import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { AgentCard } from "@/components/agents/AgentCard";
import { AgentDrawer } from "@/components/agents/AgentDrawer";
import { ImportPackModal } from "@/components/agents/ImportPackModal";
import { filterAgents, type OriginFilter } from "@/components/agents/agentFilter";
import { AgentScorecardPanel } from "@/components/workflows/AgentScorecardPanel";
import { useAgentDefinitions } from "@/hooks/use-agents";

type EditorState = { mode: "create" } | { mode: "edit"; id: string } | null;

/**
 * Agents — the team's reusable personas as a schedulable agent bench. Each agent is a card that reads like a
 * working unit: a role-tinted avatar + role badge (a display heuristic), its @handle, and its loadout (model /
 * autonomy / tools chips + the skills it carries from the AgentSkillBinding join). A persona is harness-AGNOSTIC,
 * so there's deliberately no per-card harness field — the per-harness split lives in the Fleet-health scorecard,
 * which sits BELOW the bench (measurement second, the units first). "New agent" + a card click open the editor;
 * "Import" opens the import-from-URL pack modal. (Per-agent performance isn't aggregated yet — cards show "No
 * recent runs" until that backend slice lands.)
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/agents/")({
  component: AgentsListPage,
});

function AgentsListPage() {
  const agents = useAgentDefinitions();
  const rows = agents.data ?? [];

  const [query, setQuery] = useState("");
  const [origin, setOrigin] = useState<OriginFilter>("all");
  const [editor, setEditor] = useState<EditorState>(null);
  const [importing, setImporting] = useState(false);

  const openNew = () => setEditor({ mode: "create" });
  const openAgent = (id: string) => setEditor({ mode: "edit", id });

  const importedCount = rows.filter((a) => a.origin === "Imported").length;
  const authoredCount = rows.length - importedCount;
  const visible = filterAgents(rows, query, origin);

  const hasAgents = !agents.isLoading && !agents.error && rows.length > 0;

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
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, margin: "16px 0 12px", flexWrap: "wrap" }}>
              <div className="ct-search">
                <Ic.Search size={14} />
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Search or ask — “security reviewer”, “backend implementer”…"
                  aria-label="Search agents"
                />
              </div>
              <div className="ct-tabs">
                <OriginTab value="all" current={origin} count={rows.length} onSelect={setOrigin}>All</OriginTab>
                <OriginTab value="Authored" current={origin} count={authoredCount} onSelect={setOrigin}>Authored</OriginTab>
                <OriginTab value="Imported" current={origin} count={importedCount} onSelect={setOrigin}>Imported</OriginTab>
              </div>
            </div>

            <div className="ab-section-lbl">Team bench · {visible.length}</div>

            {visible.length === 0 ? (
              <div className="ct-empty">
                <div className="ct-empty-h">No matching agents</div>
                <div className="ct-empty-p">No agent matches the current search and filter.</div>
              </div>
            ) : (
              <div className="ab-grid">
                {visible.map((a) => <AgentCard key={a.id} agent={a} onOpen={() => openAgent(a.id)} />)}
              </div>
            )}
          </>
        )}

        {/* Fleet health — the team's success / latency / spend over its run history. Measurement SECOND: it
            sits beneath the bench. Rendered once the list resolves regardless of agent count, so a team that has
            historical scored runs but no current agents still sees its rollup (self-contained, own team fetch). */}
        {!agents.isLoading && !agents.error && (
          <div className="ab-fleet">
            <AgentScorecardPanel />
          </div>
        )}
      </div>

      {editor && (
        <AgentDrawer
          mode={editor.mode}
          agentId={editor.mode === "edit" ? editor.id : undefined}
          onClose={() => setEditor(null)}
        />
      )}

      {importing && <ImportPackModal onClose={() => setImporting(false)} />}
    </section>
  );
}

/** One origin filter toggle — the warm underline-tab look, but a keyboard-operable toggle (role=button + Enter/Space), matching the accessible tab pattern in AgentDetailTabs / SettingsLayout. */
function OriginTab({ value, current, count, onSelect, children }: { value: OriginFilter; current: OriginFilter; count: number; onSelect: (v: OriginFilter) => void; children: React.ReactNode }) {
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
      {children}<span className="ct-tab-c">{count}</span>
    </span>
  );
}
