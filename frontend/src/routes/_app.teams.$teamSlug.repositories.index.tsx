import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect } from "react";

/**
 * Phase 3.0 — the standalone "Repositories" list page is gone. Repositories
 * are now scoped to a Project; discovery happens via
 * <c>/teams/{slug}/projects/{projectId}</c>'s Repositories tab, not via a
 * team-wide list.
 *
 * <para>This route survives only as a redirect so the previously-bookmarked
 * URL <c>/teams/{slug}/repositories</c> (and the older sidebar's primary
 * link) keeps landing somewhere useful — the team's projects list — rather
 * than 404-ing. New code should NEVER `navigate({ to: "/teams/$teamSlug/repositories" })`;
 * point at <c>/teams/$teamSlug/projects</c> instead.</para>
 *
 * <para>The per-repo detail subtree (<c>/repositories/{fullPath}/...</c>)
 * is still real and reachable from the project detail page's Repositories
 * tab — only this top-level list view was retired.</para>
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/")({
  component: LegacyTeamRepositoriesRedirect,
});

function LegacyTeamRepositoriesRedirect() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();

  // replace: true so the bounce doesn't pollute browser history — pressing
  // Back from the projects list should go where the user was before this URL,
  // not back to this dead intermediate.
  useEffect(() => {
    navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug }, replace: true });
  }, [teamSlug, navigate]);

  return null;
}
