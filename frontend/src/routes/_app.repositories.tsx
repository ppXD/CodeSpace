import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect } from "react";

import { teamToUrlSlug, useMe } from "@/hooks/use-me";

/**
 * Legacy `/repositories` URL — kept as a redirect so previously-bookmarked
 * non-team-scoped links don't 404.
 *
 * <para>Phase 3.0 — forwards to <c>/teams/{active-slug}/projects</c>: repositories
 * now live inside a Project, so the closest equivalent destination for someone
 * landing here is the team's project list (from which they pick a project +
 * click the Repositories tab).</para>
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

    navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: teamToUrlSlug(active) }, replace: true });
  }, [me.data, navigate]);

  return null;
}
