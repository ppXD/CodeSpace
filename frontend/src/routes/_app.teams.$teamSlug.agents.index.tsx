import { createFileRoute } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { AgentScorecardPanel } from "@/components/workflows/AgentScorecardPanel";
import { useAgentDefinitions } from "@/hooks/use-agents";

/**
 * Agents library — the team's reusable personas (AgentDefinition). Same compact header rhythm
 * as the Workflows + Repositories lists. Read-only for now: a persona is a system prompt + model
 * + tools you @-mention from a workflow's agent.code node. Authoring + pack import land as the
 * "New" + "Import" actions in follow-ups; this slice proves the nav + data wiring.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/agents/")({
  component: AgentsListPage,
});

function AgentsListPage() {
  const agents = useAgentDefinitions();
  const rows = agents.data ?? [];

  return (
    <section className="ct">
      {/* paddingBottom matches the Workflows list — without a tabs strip the title row would
          otherwise sit flush against the table border below. */}
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs">
          <span className="cur">Agents</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Agents</h1>
        </div>
      </div>

      <div className="ct-body">
        {/* The measurement spine — the team's agent success rate + latency over its run history, above the
            persona library. Self-contained (own fetch, team-scoped at the source); renders nothing while
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
            <div className="ct-empty-p">Reusable personas — a system prompt, model, and tools you can <strong>@-mention</strong> from a workflow — will appear here.</div>
          </div>
        )}

        {!agents.isLoading && !agents.error && rows.length > 0 && (
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "42%" }}>Agent</th>
                <th>Model</th>
                <th>Tools</th>
                <th>Origin</th>
                <th>Added</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((a) => (
                <tr key={a.id}>
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
                  <td><ToolsCell tools={a.tools} /></td>
                  <td><span className="wf-trigger-muted">{a.origin === "Imported" ? "imported" : "authored"}</span></td>
                  <td>{formatRelative(a.createdDate)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
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
      {tools.map((t) => <span key={t} className="wf-trigger-chip">{t}</span>)}
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
