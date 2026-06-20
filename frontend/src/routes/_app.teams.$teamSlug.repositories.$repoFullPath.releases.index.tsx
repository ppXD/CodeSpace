import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ReleasesPanel } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * Releases list at `/teams/{slug}/repositories/{fullPath}/releases` — reached from the Code tab's
 * Releases card. Releases / Tags tab + page are URL search params (shareable). "Back to code" goes
 * DIRECTLY to the Code tab (the list's only sensible parent), not the history stack — unlike a detail
 * page, where back returns to wherever you came from. Lives at the index child so the `$tag` detail
 * sibling doesn't inherit the list's search.
 */
type ReleasesTab = "releases" | "tags";

interface ReleasesSearch {
  tab: ReleasesTab;
  page: number;
}

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/releases/")({
  validateSearch: (raw: Record<string, unknown>): ReleasesSearch => {
    const tab: ReleasesTab = String(raw.tab ?? "releases") === "tags" ? "tags" : "releases";
    const rawPage = Number(raw.page);
    const page = Number.isFinite(rawPage) && rawPage >= 1 ? Math.floor(rawPage) : 1;
    return { tab, page };
  },
  component: ReleasesListRoute,
});

function ReleasesListRoute() {
  const { teamSlug, repoFullPath } = Route.useParams();
  const { tab, page } = Route.useSearch();
  const navigate = useNavigate();

  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  const encoded = encodeURIComponent(fullPath);

  return (
    <ReleasesPanel
      repoId={repo.id}
      tab={tab}
      page={page}
      onTabChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/releases",
        params: { teamSlug, repoFullPath: encoded },
        search: { tab: next, page: 1 },
      })}
      onPageChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/releases",
        params: { teamSlug, repoFullPath: encoded },
        search: { tab, page: next },
      })}
      onSelectRelease={(rtag) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/releases/$tag",
        params: { teamSlug, repoFullPath: encoded, tag: encodeURIComponent(rtag) },
      })}
      // The releases list lives under Code — "Back to code" always returns there, not via history.
      onBack={() => navigate({ to: "/teams/$teamSlug/repositories/$repoFullPath/code", params: { teamSlug, repoFullPath: encoded } })}
    />
  );
}
