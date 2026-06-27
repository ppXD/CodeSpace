import { createFileRoute } from "@tanstack/react-router";

import { LibraryPage } from "@/components/library/LibraryPage";

/**
 * Library / store — the team's imported packs (agent + skill source libraries) as browsable categories with
 * per-pack freshness. Kept thin (component lives in components/library) so the route module exports only its
 * Route, mirroring the Settings shell.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/library")({
  component: LibraryPage,
});
