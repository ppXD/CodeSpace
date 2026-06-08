import type { ReactNode } from "react";

/**
 * One tab in the Agent-detail shell. The set is data-driven so adding a view (Settings, Logs,
 * future) is one array entry — no shell change. `key` is the stable identifier the parent
 * switches on; `label` is what the user reads; `icon` is optional leading glyph.
 */
export interface AgentTab {
  key: string;
  label: string;
  icon?: ReactNode;
}

/**
 * Presentational segmented tab strip for the Agent detail (Overview · Activity · Source · …).
 * Pure + controlled: it renders the tabs and reports clicks; the parent owns which is active and
 * what each renders. Kept dumb so it's trivially testable and reusable for any tabbed surface.
 */
export function AgentDetailTabs({ tabs, active, onChange }: {
  tabs: ReadonlyArray<AgentTab>;
  active: string;
  onChange: (key: string) => void;
}) {
  return (
    <nav className="agent-tabs" role="tablist" aria-label="Agent views">
      {tabs.map((t) => (
        <button
          key={t.key}
          type="button"
          role="tab"
          aria-selected={t.key === active}
          data-active={t.key === active}
          className="agent-tab"
          onClick={() => onChange(t.key)}
        >
          {t.icon && <span className="agent-tab-ic" aria-hidden>{t.icon}</span>}
          <span className="agent-tab-lbl">{t.label}</span>
        </button>
      ))}
    </nav>
  );
}
