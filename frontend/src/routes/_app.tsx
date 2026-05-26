import { Outlet, createFileRoute, redirect } from "@tanstack/react-router";
import { useState } from "react";

import { isAuthenticated } from "@/api/auth";
import { Ic } from "@/_imported/ai-code-space/icons";
import { Sidebar } from "@/_imported/ai-code-space/sidebar";

import "@/styles/ai-code-space.css";

/**
 * Authenticated-shell layout. Sits above every URL the operator browses through
 * (repos, repo-detail, PRs, PR-detail, the placeholder tabs) so the sidebar +
 * collapse chrome stay mounted across navigations — no re-render storms when
 * clicking between tabs.
 *
 * Auth gate runs `beforeLoad` so unauthenticated users never see this shell at
 * all — they get bounced to /signin before any child component mounts. Cheap
 * localStorage check; mid-session 401s are caught downstream in api/request and
 * bounce out separately.
 */
export const Route = createFileRoute("/_app")({
  beforeLoad: () => {
    if (!isAuthenticated()) throw redirect({ to: "/signin" });
  },
  component: AppLayout,
});

function AppLayout() {
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  return (
    <div className="acs-root">
      <div
        className="app"
        data-sidebar={sidebarCollapsed ? "collapsed" : "expanded"}
        data-density="regular"
      >
        <Sidebar />
        <Outlet />

        <div className="float-chrome float-l">
          <button
            className="chrome-btn"
            onClick={() => setSidebarCollapsed(c => !c)}
            title={sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"}
          >
            {sidebarCollapsed ? <Ic.ChevronRight size={14} /> : <Ic.ChevronLeft size={14} />}
          </button>
        </div>
      </div>
    </div>
  );
}
