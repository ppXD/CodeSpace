import { createFileRoute, redirect } from "@tanstack/react-router";

/**
 * Legacy deep-link redirect. The run-detail page (the Run Room) moved out of the workflows subtree to the
 * team-level /teams/{slug}/runs/{runId} — a run is run-neutral (it can come from any source, not just a workflow),
 * so Runs is its home. This route survives only to bounce previously shared / bookmarked
 * …/workflows/runs/{runId} links to the new canonical URL, passing the runId (a GUID) as the ref —
 * the run-detail page's by-ref resolver then canonicalises it to /runs/{number}. New code never navigates here.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/runs/$runId")({
  beforeLoad: ({ params }) => {
    throw redirect({
      to: "/teams/$teamSlug/runs/$runNumber",
      params: { teamSlug: params.teamSlug, runNumber: params.runId },
    });
  },
});
