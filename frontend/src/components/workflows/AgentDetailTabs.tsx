/**
 * One tab in the Agent-detail shell. Data-driven so adding a view (Settings, Logs, future) is one
 * array entry — no shell change. `count` shows the standard `.ct-tab-c` badge (like the Project
 * page's "Repositories 5" / "Variables 2").
 */
export interface AgentTab {
  key: string;
  label: string;
  count?: number;
}

/**
 * Presentational tab strip for the Agent detail. Emits the app's shared `.ct-tabs` / `.ct-tab`
 * underline tabs (the same markup the Project page uses for Repositories | Variables) so the
 * surface is visually consistent across the product. Pure + controlled: the parent owns which is
 * active and what each renders.
 */
export function AgentDetailTabs({ tabs, active, onChange }: {
  tabs: ReadonlyArray<AgentTab>;
  active: string;
  onChange: (key: string) => void;
}) {
  return (
    <div className="ct-tabs" role="tablist" aria-label="Agent views">
      {tabs.map((t) => (
        <div
          key={t.key}
          role="tab"
          tabIndex={0}
          aria-selected={t.key === active}
          data-active={t.key === active}
          className="ct-tab"
          onClick={() => onChange(t.key)}
          onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onChange(t.key); } }}
        >
          {t.label}
          {t.count != null && <span className="ct-tab-c">{t.count}</span>}
        </div>
      ))}
    </div>
  );
}
