import { useCallback, useState, type ReactNode } from "react";

import { AgentDetailTabs, type AgentTab as BaseAgentTab } from "./AgentDetailTabs";

/**
 * A tab in the Agent-detail shell.
 *  - `keepMounted`: stay mounted (just hidden) once first visited — for heavy/stateful content
 *    (the Source canvas editor with its UNSAVED edits) so tab switches never lose state.
 *  - `fill`: render the pane edge-to-edge (no `.ct-body` padding/scroll) — for the full-height
 *    canvas editor. Normal tabs render inside a padded, scrollable `.ct-body`.
 */
export interface AgentTab extends BaseAgentTab {
  keepMounted?: boolean;
  fill?: boolean;
}

/** Handle passed to each tab's renderer so its content can drive navigation (e.g. "Edit in Source"). */
export interface AgentShellApi {
  active: string;
  goTo: (key: string) => void;
}

/**
 * Generic tabbed shell for the Agent detail, built on the app's shared container-page system
 * (`.ct` › `.ct-head` { breadcrumb + `.ct-tabs` } › body) so it matches the Project / Repositories
 * pages exactly. Owns the active tab + visited set. Mount policy:
 *   - active tab → mounted + visible;
 *   - `keepMounted` tab, already visited → mounted but hidden → state preserved (the editor);
 *   - everything else → not mounted (so the heavy editor never mounts until Source is opened).
 * `render(key, api)` returns each tab's content; `api.goTo` lets content switch tabs.
 */
export function AgentDetailShell({ tabs, defaultTab, crumbs, render }: {
  tabs: ReadonlyArray<AgentTab>;
  defaultTab: string;
  crumbs?: ReactNode;
  render: (key: string, api: AgentShellApi) => ReactNode;
}) {
  const [active, setActive] = useState(defaultTab);
  const [visited, setVisited] = useState<ReadonlySet<string>>(() => new Set([defaultTab]));

  const goTo = useCallback((key: string) => {
    setVisited((v) => (v.has(key) ? v : new Set(v).add(key)));
    setActive(key);
  }, []);

  const api: AgentShellApi = { active, goTo };

  return (
    <section className="ct agent-detail">
      <div className="ct-head">
        {crumbs && <div className="ct-crumbs">{crumbs}</div>}
        <AgentDetailTabs tabs={tabs} active={active} onChange={goTo} />
      </div>
      {tabs.map((t) => {
        const isActive = t.key === active;
        const mounted = isActive || (t.keepMounted === true && visited.has(t.key));
        if (!mounted) return null;
        return (
          <div
            key={t.key}
            className={t.fill ? "agent-source-pane" : "ct-body"}
            data-tab={t.key}
            style={isActive ? undefined : { display: "none" }}
          >
            {render(t.key, api)}
          </div>
        );
      })}
    </section>
  );
}
