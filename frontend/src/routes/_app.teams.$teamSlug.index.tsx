import { createFileRoute, redirect } from "@tanstack/react-router";

/**
 * Bare `/teams/{slug}` lands the user on the team's repositories — the app's
 * first-class team page. Implemented as a `beforeLoad` redirect so the URL bar
 * always reflects the actual landing page (no flashed `/teams/{slug}` in the
 * history stack).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/")({
  beforeLoad: ({ params }) => {
    throw redirect({ to: "/teams/$teamSlug/repositories", params: { teamSlug: params.teamSlug } });
  },
});
