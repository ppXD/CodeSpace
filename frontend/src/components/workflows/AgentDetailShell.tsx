import { useCallback, useState, type ReactNode } from "react";

import { AgentDetailTabs, type AgentTab as BaseAgentTab } from "./AgentDetailTabs";

/**
 * A tab in the Agent-detail shell. Extends the presentational tab with `keepMounted`: a tab marked
 * keepMounted stays mounted (just hidden) once first visited, so heavy/stateful content — the
 * Source canvas editor with its UNSAVED edits — survives tab switches with zero data loss. Tabs
 * without it mount lazily and unmount when inactive (cheap, read-only panels).
 */
export interface AgentTab extends BaseAgentTab {
  keepMounted?: boolean;
}

/** Handle passed to each tab's renderer so its content can drive navigation (e.g. "Edit in Source"). */
export interface AgentShellApi {
  active: string;
  goTo: (key: string) => void;
}

/**
 * Generic tabbed shell for the Agent detail (Overview · Activity · ⟨/⟩ Source · …). Owns the active
 * tab + the visited set; renders a persistent tab strip (+ optional leading slot, e.g. a back
 * button) and the active tab's content. Mount policy:
 *   - active tab → mounted + visible;
 *   - keepMounted tab, already visited → mounted but hidden (display:none) → state preserved;
 *   - everything else → not mounted (so the heavy editor never mounts until Source is opened).
 * `render(key, api)` returns each tab's content; `api.goTo` lets content switch tabs.
 */
export function AgentDetailShell({ tabs, defaultTab, leading, render }: {
  tabs: ReadonlyArray<AgentTab>;
  defaultTab: string;
  leading?: ReactNode;
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
    <div className="agent-detail">
      <div className="agent-detail-tabbar">
        {leading}
        <AgentDetailTabs tabs={tabs} active={active} onChange={goTo} />
      </div>
      <div className="agent-detail-body">
        {tabs.map((t) => {
          const isActive = t.key === active;
          const mounted = isActive || (t.keepMounted === true && visited.has(t.key));
          if (!mounted) return null;
          return (
            <div
              key={t.key}
              className="agent-detail-pane"
              data-tab={t.key}
              style={isActive ? undefined : { display: "none" }}
            >
              {render(t.key, api)}
            </div>
          );
        })}
      </div>
    </div>
  );
}
