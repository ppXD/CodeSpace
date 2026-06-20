import { createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";

import { ReleasesPanel } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * Releases page at `/teams/{slug}/repositories/{fullPath}/releases` — reached from the Code tab's
 * Releases card. Releases / Tags tab + page are URL search params (shareable). Back returns to the
 * exact prior page (the Code tab) via the history stack.
 */
type ReleasesTab = "releases" | "tags";

interface ReleasesSearch {
  tab: ReleasesTab;
  page: number;
}

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/releases")({
  validateSearch: (raw: Record<string, unknown>): ReleasesSearch => {
    const tab: ReleasesTab = String(raw.tab ?? "releases") === "tags" ? "tags" : "releases";
    const rawPage = Number(raw.page);
    const page = Number.isFinite(rawPage) && rawPage >= 1 ? Math.floor(rawPage) : 1;
    return { tab, page };
  },
  component: ReleasesRoute,
});

function ReleasesRoute() {
  const { teamSlug, repoFullPath } = Route.useParams();
  const { tab, page } = Route.useSearch();
  const navigate = useNavigate();
  const router = useRouter();

  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <ReleasesPanel
      repoId={repo.id}
      tab={tab}
      page={page}
      onTabChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/releases",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        search: { tab: next, page: 1 },
      })}
      onPageChange={(next) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/releases",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        search: { tab, page: next },
      })}
      onBack={() => {
        if (router.history.canGoBack()) {
          router.history.back();
          return;
        }
        navigate({ to: "/teams/$teamSlug/repositories/$repoFullPath/code", params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) } });
      }}
    />
  );
}
