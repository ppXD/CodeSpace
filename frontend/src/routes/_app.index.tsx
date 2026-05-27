import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect } from "react";

import { teamToUrlSlug, useMe } from "@/hooks/use-me";

/**
 * Root `/` under the authenticated shell — resolves the user's active team and
 * bounces to `/teams/{slug}/projects`. We can't do this in `beforeLoad` because
 * the team list lives in React Query / /me, not in synchronous storage; a
 * component-level redirect waits for the /me payload and then routes.
 *
 * <para>Phase 3.0 — destination is `/projects`, not `/repositories`. Projects
 * is the new primary nav row; the team-wide repositories list no longer
 * exists as a discoverable surface.</para>
 *
 * While /me is loading, this renders nothing (the layout already provides the
 * sidebar shell). Once data lands, the effect fires exactly once and the
 * landing URL settles to `/teams/{active-slug}/projects`.
 */
export const Route = createFileRoute("/_app/")({
  component: RootRedirect,
});

const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

function RootRedirect() {
  const me = useMe();
  const navigate = useNavigate();

  useEffect(() => {
    const teams = me.data?.teams;
    if (!teams || teams.length === 0) return;

    // Prefer the team the user picked last (persisted by the sidebar switcher).
    // Fall back to the first team in the list — same heuristic useActiveTeam
    // uses, so the landing experience matches what the sidebar will show.
    const storedId = localStorage.getItem(ACTIVE_TEAM_STORAGE_KEY);
    const active = teams.find(t => t.id === storedId) ?? teams[0];

    navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: teamToUrlSlug(active) }, replace: true });
  }, [me.data, navigate]);

  return null;
}
