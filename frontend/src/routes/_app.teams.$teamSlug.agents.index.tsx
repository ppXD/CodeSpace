import { createFileRoute } from "@tanstack/react-router";
import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentBoundSkill } from "@/api/agents";
import { ApiError } from "@/api/request";
import { AgentEditorModal } from "@/components/agents/AgentEditor";
import { ImportPackModal } from "@/components/agents/ImportPackModal";
import { filterAgents, type OriginFilter } from "@/components/agents/agentFilter";
import { AgentScorecardPanel } from "@/components/workflows/AgentScorecardPanel";
import { useAgentDefinitions } from "@/hooks/use-agents";

type EditorState = { mode: "create" } | { mode: "edit"; id: string } | null;

/**
 * Agents library — the team's reusable personas (AgentDefinition). The measurement strip (success / latency /
 * spend) sits above a searchable, origin-filtered table of personas; each row shows the persona's composition —
 * model, the skills it carries (the AgentSkillBinding join), and its tool allow-list — plus whether it was
 * authored locally or imported from a pack. A persona is harness-AGNOSTIC (it runs on any compatible harness),
 * so there's deliberately no per-row harness column — the per-harness split lives in the scorecard above.
 * "New agent" + a row click open the editor; pack import lands as the "Import" action in a later slice.
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
            <button type="button" className="btn" onClick={() => setImporting(true)}><Ic.Download size={14} /> Import</button>
            <button type="button" className="btn btn-primary" onClick={openNew}><Ic.Plus size={14} /> New agent</button>
          </div>
        </div>
        {hasAgents && (
          <div className="ct-sub">
            {rows.length} {rows.length === 1 ? "persona" : "personas"}
            {importedCount > 0 && ` · ${importedCount} from packs`}
          </div>
        )}
      </div>

      <div className="ct-body">
        {/* The measurement spine — the team's agent success rate + latency + estimated spend over its run history,
            above the persona library. Self-contained (own fetch, team-scoped at the source); renders nothing while
            loading so the list below isn't pushed around. */}
        <AgentScorecardPanel />

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
            <div className="ct-empty-p">Reusable personas — a system prompt, model, skills, and tools you can <strong>@-mention</strong> from a workflow — will appear here.</div>
            <div style={{ display: "flex", gap: 8, justifyContent: "center", marginTop: 14 }}>
              <button type="button" className="btn" onClick={() => setImporting(true)}><Ic.Download size={14} /> Import a pack</button>
              <button type="button" className="btn btn-primary" onClick={openNew}><Ic.Plus size={14} /> New agent</button>
            </div>
          </div>
        )}

        {hasAgents && (
          <>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, margin: "16px 0 6px", flexWrap: "wrap" }}>
              <div className="ct-search">
                <Ic.Search size={14} />
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Search agents…"
                  aria-label="Search agents"
                />
              </div>
              <div className="ct-tabs">
                <OriginTab value="all" current={origin} count={rows.length} onSelect={setOrigin}>All</OriginTab>
                <OriginTab value="Authored" current={origin} count={authoredCount} onSelect={setOrigin}>Authored</OriginTab>
                <OriginTab value="Imported" current={origin} count={importedCount} onSelect={setOrigin}>Imported</OriginTab>
              </div>
            </div>

            {visible.length === 0 ? (
              <div className="ct-empty">
                <div className="ct-empty-h">No matching agents</div>
                <div className="ct-empty-p">No persona matches the current search and filter.</div>
              </div>
            ) : (
              <table className="tbl">
                <thead>
                  <tr>
                    <th style={{ width: "38%" }}>Agent</th>
                    <th>Model</th>
                    <th>Skills</th>
                    <th>Tools</th>
                    <th>Origin</th>
                    <th>Added</th>
                  </tr>
                </thead>
                <tbody>
                  {visible.map((a) => (
                    <tr
                      key={a.id}
                      tabIndex={0}
                      aria-label={`Edit ${a.name}`}
                      onClick={() => openAgent(a.id)}
                      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openAgent(a.id); } }}
                    >
                      <td>
                        <div className="repo-cell">
                          <div className="repo-mark" style={{ background: "var(--accent-soft)", color: "var(--accent)" }}>
                            <Ic.Bot size={14} />
                          </div>
                          <div className="repo-info">
                            <div className="repo-name">
                              {a.name}
                              <span className="wf-trigger-muted" style={{ marginLeft: 8 }}>@{a.slug}</span>
                            </div>
                            {a.description && <div className="repo-path"><span className="repo-path-desc" title={a.description}>{a.description}</span></div>}
                          </div>
                        </div>
                      </td>
                      <td>{a.model ? <span className="wf-version">{a.model}</span> : <span className="wf-trigger-muted">default</span>}</td>
                      <td><SkillsCell skills={a.boundSkills} /></td>
                      <td><ToolsCell tools={a.tools} /></td>
                      <td><span className="wf-trigger-muted">{a.origin === "Imported" ? "imported" : "authored"}</span></td>
                      <td>{formatRelative(a.createdDate)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
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

/**
 * The skills a persona carries (the AgentSkillBinding join), as accent chips by handle. Caps the visible chips
 * so a heavily-bound persona doesn't blow out the row; the rest fold into a muted "+N". Empty = a muted "none".
 */
function SkillsCell({ skills }: { skills: AgentBoundSkill[] }) {
  if (!skills || skills.length === 0) return <span className="wf-trigger-muted">none</span>;

  const shown = skills.slice(0, 3);
  const extra = skills.length - shown.length;

  return (
    <div className="wf-triggers">
      {shown.map((s) => <span key={s.skillDefinitionId} className="wf-trigger-chip" title={s.name}>{s.slug}</span>)}
      {extra > 0 && <span className="wf-trigger-muted">+{extra}</span>}
    </div>
  );
}

/**
 * Tools tri-state: null = inherits the harness default toolset; [] = explicitly no tools;
 * a list renders as chips. Matches the backend AgentDefinition.tools null-vs-empty contract.
 */
function ToolsCell({ tools }: { tools: string[] | null }) {
  if (tools === null) return <span className="wf-trigger-muted">default</span>;
  if (tools.length === 0) return <span className="wf-trigger-muted">none</span>;

  return (
    <div className="wf-triggers">
      {tools.map((t) => <span key={t} className="wf-trigger-chip wf-trigger-chip-soft">{t}</span>)}
    </div>
  );
}

function formatRelative(iso: string): string {
  const date = new Date(iso);
  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);

  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 86400 * 30) return `${Math.floor(seconds / 86400)}d ago`;

  return date.toLocaleDateString();
}
