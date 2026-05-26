import { createFileRoute, redirect } from "@tanstack/react-router";

/**
 * Bare `/teams/{slug}/repositories/{fullPath}` lands the user on the Overview tab.
 * Implemented as a redirect instead of an empty render so the URL bar always
 * reflects the actual page — pasting the link drops you on the canonical
 * `/overview` URL, which is also what the breadcrumb / shared link will show.
 *
 * `params.repoFullPath` is already the URL-encoded form (TanStack hands it
 * through as the raw URL segment for redirect targets) — no re-encoding here.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/")({
  beforeLoad: ({ params }) => {
    throw redirect({
      to: "/teams/$teamSlug/repositories/$repoFullPath/overview",
      params: { teamSlug: params.teamSlug, repoFullPath: params.repoFullPath },
    });
  },
});
