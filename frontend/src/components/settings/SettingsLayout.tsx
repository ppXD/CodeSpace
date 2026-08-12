import { Outlet, useLocation, useNavigate, useParams } from "@tanstack/react-router";

/**
 * Team Settings shell — owns the "Settings" header + the section tab strip; each section renders its body via
 * <Outlet/> (the app's `ct` + `.ct-tabs` rhythm, like the repo-detail / agent-detail shells). Team-scoped
 * configuration lives here (model credentials today; git providers next) so the primary sidebar stays focused
 * on the work objects. Kept in its own file (not the route) so the route module exports only its `Route`.
 */
export function SettingsLayout() {
  const { teamSlug } = useParams({ from: "/_app/teams/$teamSlug/settings" });
  const navigate = useNavigate();
  const pathname = useLocation().pathname;

  const tabs = [
    { key: "model-credentials", label: "Model credentials", to: "/teams/$teamSlug/settings/model-credentials" },
    { key: "providers", label: "Providers", to: "/teams/$teamSlug/settings/providers" },
  ] as const;

  const active = tabs.find(t => pathname.includes(`/settings/${t.key}`))?.key ?? tabs[0].key;

  return (
    <section className="ct">
      <div className="ct-head">
        <div className="ct-crumbs"><span className="cur">Settings</span></div>
        <div className="ct-title-row"><h1 className="ct-title">Settings</h1></div>
        <div className="ct-tabs" role="tablist" aria-label="Settings sections">
          {tabs.map((t) => (
            <div
              key={t.key}
              role="tab"
              tabIndex={0}
              aria-selected={t.key === active}
              data-active={t.key === active}
              className="ct-tab"
              onClick={() => navigate({ to: t.to, params: { teamSlug } })}
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); navigate({ to: t.to, params: { teamSlug } }); } }}
            >
              {t.label}
            </div>
          ))}
        </div>
      </div>

      <div className="ct-body"><Outlet /></div>
    </section>
  );
}
