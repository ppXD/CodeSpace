import { createFileRoute, redirect } from "@tanstack/react-router";

/**
 * Bare `/teams/{slug}` lands the user on the team's project list. Phase 3.0
 * promoted Projects to the team's first-class landing page; repositories live
 * inside a project's Repositories tab.
 *
 * <para>Implemented as a `beforeLoad` redirect so the URL bar always reflects
 * the actual landing page (no flashed `/teams/{slug}` in the history stack).</para>
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/")({
  beforeLoad: ({ params }) => {
    throw redirect({ to: "/teams/$teamSlug/projects", params: { teamSlug: params.teamSlug } });
  },
});
