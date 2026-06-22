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
 *
 * Active tab is controlled when `active` + `onActiveChange` are supplied (the route drives it from
 * the URL `?tab=` so links are deep-linkable), and uncontrolled otherwise. Either way the shell
 * owns the `visited` set so `keepMounted` panes survive once shown — including when the controlled
 * tab changes from the URL on load.
 */
export function AgentDetailShell({ tabs, defaultTab, crumbs, render, active: controlledActive, onActiveChange, hideTabs = false }: {
  tabs: ReadonlyArray<AgentTab>;
  defaultTab: string;
  crumbs?: ReactNode;
  render: (key: string, api: AgentShellApi) => ReactNode;
  active?: string;
  onActiveChange?: (key: string) => void;
  /** Suppress the tab BAR while keeping the pane/mount machinery — navigation is driven by content
   *  (e.g. an "Edit in Source" button) + the breadcrumb. Used when there's effectively one landing view. */
  hideTabs?: boolean;
}) {
  const [internalActive, setInternalActive] = useState(defaultTab);
  const active = controlledActive ?? internalActive;

  // Tabs shown so far — a keepMounted pane (the Source editor) stays mounted-but-hidden once
  // visited. Seeded with the mount-time active (covers a deep-link straight to ?tab=source) and
  // extended in goTo, the path every tab click + in-content nav goes through.
  const [visited, setVisited] = useState<ReadonlySet<string>>(() => new Set([controlledActive ?? defaultTab]));

  const goTo = useCallback((key: string) => {
    setVisited((v) => (v.has(key) ? v : new Set(v).add(key)));
    if (onActiveChange) onActiveChange(key);
    else setInternalActive(key);
  }, [onActiveChange]);

  const api: AgentShellApi = { active, goTo };

  return (
    <section className="ct agent-detail">
      {/* The .ct-head border-bottom exists to host the tab underline. With no tabs it's just an orphan
          line under the breadcrumb, so drop it — the content below brings its own structure (matching the
          clean detail-page header rather than the tabbed project/repo heads where the border IS the tab track). */}
      <div className="ct-head" style={hideTabs ? { borderBottom: "none" } : undefined}>
        {crumbs && <div className="ct-crumbs">{crumbs}</div>}
        {!hideTabs && <AgentDetailTabs tabs={tabs} active={active} onChange={goTo} />}
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
