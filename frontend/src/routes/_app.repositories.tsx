import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect } from "react";

import { teamToUrlSlug, useMe } from "@/hooks/use-me";

/**
 * Legacy `/repositories` URL — kept as a redirect so previously-bookmarked
 * non-team-scoped links don't 404. Forwards to `/teams/{active-slug}/repositories`
 * via the same "active team" resolution as the root index route.
 *
 * If we ever delete this file, the route just becomes a 404 — no data dependency
 * on it elsewhere, so removal is safe whenever bookmarked links are no longer
 * a concern.
 */
export const Route = createFileRoute("/_app/repositories")({
  component: LegacyRepositoriesRedirect,
});

const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

function LegacyRepositoriesRedirect() {
  const me = useMe();
  const navigate = useNavigate();

  useEffect(() => {
    const teams = me.data?.teams;
    if (!teams || teams.length === 0) return;

    const storedId = localStorage.getItem(ACTIVE_TEAM_STORAGE_KEY);
    const active = teams.find(t => t.id === storedId) ?? teams[0];

    navigate({ to: "/teams/$teamSlug/repositories", params: { teamSlug: teamToUrlSlug(active) }, replace: true });
  }, [me.data, navigate]);

  return null;
}
