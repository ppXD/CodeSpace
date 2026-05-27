import { createFileRoute } from "@tanstack/react-router";
import { useState } from "react";

import { useCredentials } from "@/hooks/use-credentials";
import { ConnectRemoteModal } from "@/_imported/ai-code-space/connect-remote-modal";
import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * Team-scoped Settings page. First section: Connections. As the team-admin
 * surface grows (Variables, Members), each lands here as a new section.
 *
 * Why this exists: after Phase 3.0 collapsed the sidebar to Projects + Workflows,
 * there was no home for team-admin tasks. Connections didn't fit anywhere —
 * not in the user popover (credentials are team-scoped, not user-scoped), not
 * in the team switcher (the switcher's job is switching, not managing). A
 * dedicated /teams/{slug}/settings page is the standard SaaS pattern (Linear,
 * Notion, Vercel) and gives every future team-admin action a stable home.
 *
 * The Connections section is intentionally a thin shell: a brief description
 * and a "Manage connections" button that opens the existing ConnectRemoteModal,
 * which already handles list + add + revoke. Avoids duplicating the modal's
 * UI inline on the page — when Variables / Members sections land we can extract
 * a shared `.settings-section` style at that point (Karpathy rule: no
 * abstractions for single-use code).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings")({
  component: TeamSettingsPage,
});

function TeamSettingsPage() {
  const credentials = useCredentials();
  const [connectOpen, setConnectOpen] = useState(false);

  const activeCount = (credentials.data ?? []).filter(c => c.status === "Active").length;

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 14 }}>
        <div className="ct-crumbs">
          <span className="cur">Settings</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Settings</h1>
        </div>
      </div>

      <div className="ct-body">
        <div style={{ maxWidth: 720, padding: "20px 28px" }}>
          {/* Inline styles rather than a `.settings-section` class — only one
              section exists today. When Variables / Members land we'll extract. */}
          <div style={{ borderBottom: "1px solid var(--line)", paddingBottom: 24 }}>
            <div style={{ fontSize: 15, fontWeight: 600, color: "var(--ink)", marginBottom: 4 }}>
              Connections
            </div>
            <div style={{ fontSize: 13, color: "var(--muted)", marginBottom: 14, lineHeight: 1.5 }}>
              OAuth credentials this team uses to talk to GitHub / GitLab. Credentials are
              team-scoped — adding one here makes it available to every repository in this
              team.
              {activeCount > 0 && ` ${activeCount} active connection${activeCount === 1 ? "" : "s"}.`}
            </div>
            <button className="btn" onClick={() => setConnectOpen(true)}>
              <Ic.Link size={14} /> Manage connections
            </button>
          </div>
        </div>
      </div>

      {connectOpen && <ConnectRemoteModal onClose={() => setConnectOpen(false)} />}
    </section>
  );
}
