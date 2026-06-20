import { createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";

import { ReleaseDetailRoute } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * Release detail at `/teams/{slug}/repositories/{fullPath}/releases/{tag}`. The tag is URL-encoded so
 * dotted/slashed tags survive as one segment (same trick as repoFullPath). "Back to releases" uses the
 * history stack — it returns to the exact releases/tags tab + page the user came from (like the PR/issue
 * detail), unlike the releases LIST whose back goes straight to Code.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/releases/$tag")({
  component: ReleaseDetailPageRoute,
});

function ReleaseDetailPageRoute() {
  const { teamSlug, repoFullPath, tag: tagParam } = Route.useParams();
  const navigate = useNavigate();
  const router = useRouter();

  const fullPath = decodeURIComponent(repoFullPath);
  const tag = decodeURIComponent(tagParam);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <ReleaseDetailRoute
      repoId={repo.id}
      tag={tag}
      onBack={() => {
        if (router.history.canGoBack()) {
          router.history.back();
          return;
        }
        navigate({
          to: "/teams/$teamSlug/repositories/$repoFullPath/releases",
          params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
          search: { tab: "releases", page: 1 },
        });
      }}
    />
  );
}
