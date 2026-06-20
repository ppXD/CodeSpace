import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { IssuesListBody } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * Issues list at `/teams/{slug}/repositories/{fullPath}/issues`. State filter + page are URL search
 * params so deep links like `.../issues?state=Closed&page=3` are shareable. Lives at the index child so
 * the `$number` detail (a sibling) doesn't inherit the list's required search. Mirrors the Pulls split.
 */
type IssueFilter = "Open" | "Closed";

interface IssuesSearch {
  state: IssueFilter;
  page: number;
}

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/issues/")({
  validateSearch: (raw: Record<string, unknown>): IssuesSearch => {
    const rawState = String(raw.state ?? "Open");
    const state: IssueFilter = rawState === "Closed" ? "Closed" : "Open";

    const rawPage = Number(raw.page);
    const page = Number.isFinite(rawPage) && rawPage >= 1 ? Math.floor(rawPage) : 1;

    return { state, page };
  },
  component: IssuesRoute,
});

function IssuesRoute() {
  const { teamSlug, repoFullPath } = Route.useParams();
  const { state, page } = Route.useSearch();
  const navigate = useNavigate();

  // URL uses the readable fullPath; the list body still takes the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <IssuesListBody
      repoId={repo.id}
      filter={state}
      page={page}
      onFilterChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/issues",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        // Filter change resets to page 1 — paging through "Closed" then clicking "Open" should
        // land on page 1 of Open, not page 17 of whatever happens to live there.
        search: { state: next, page: 1 },
      })}
      onPageChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/issues",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        search: { state, page: next },
      })}
      onSelectIssue={(number) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/issues/$number",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath), number: String(number) },
      })}
    />
  );
}
