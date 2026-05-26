import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { PullRequestsListBody } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * PR list at `/teams/{slug}/repositories/{fullPath}/pulls`. State filter + page are
 * URL search params so deep links like
 * `/teams/{slug}/repositories/{fullPath}/pulls?state=Closed&page=3` are shareable.
 * Clicking a row navigates to `.../pulls/{number}` — also shareable.
 *
 * `$repoFullPath` is the URL-encoded provider fullPath; we decode once at the route
 * edge to recover the readable form and resolve to a UUID for downstream API calls.
 */
type PrFilter = "Open" | "Closed";

interface PullsSearch {
  state: PrFilter;
  page: number;
}

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/pulls/")({
  validateSearch: (raw: Record<string, unknown>): PullsSearch => {
    const rawState = String(raw.state ?? "Open");
    const state: PrFilter = rawState === "Closed" ? "Closed" : "Open";

    const rawPage = Number(raw.page);
    const page = Number.isFinite(rawPage) && rawPage >= 1 ? Math.floor(rawPage) : 1;

    return { state, page };
  },
  component: PullsListRoute,
});

function PullsListRoute() {
  const { teamSlug, repoFullPath } = Route.useParams();
  const { state, page } = Route.useSearch();
  const navigate = useNavigate();

  // URL uses the readable fullPath; the list body still takes the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <PullRequestsListBody
      repoId={repo.id}
      filter={state}
      page={page}
      onFilterChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/pulls",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        // Filter change resets to page 1 — paging through "Closed" then clicking
        // "Open" should land on page 1 of Open, not page 17 of whatever happens
        // to live there.
        search: { state: next, page: 1 },
      })}
      onPageChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/pulls",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        search: { state, page: next },
      })}
      onSelectPr={(number) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/pulls/$number",
        // URL params are always strings; coerce here so the calling component
        // can deal in plain numbers.
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath), number: String(number) },
      })}
    />
  );
}
