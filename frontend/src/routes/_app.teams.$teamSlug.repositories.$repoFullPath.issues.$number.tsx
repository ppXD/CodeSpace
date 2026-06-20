import { createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";

import { IssueDetailRoute } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * Issue detail at `/teams/{slug}/repositories/{fullPath}/issues/{number}`. The number is a path param so
 * the URL stands alone (deep-linkable). Back returns to the EXACT prior list page (filter + page) via the
 * history stack, falling back to the default list only when there's no in-app history.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/issues/$number")({
  component: IssueDetailPageRoute,
});

function IssueDetailPageRoute() {
  const { teamSlug, repoFullPath, number: numberParam } = Route.useParams();
  const number = Number(numberParam);
  const navigate = useNavigate();
  const router = useRouter();

  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <IssueDetailRoute
      repoId={repo.id}
      number={number}
      onBack={() => {
        if (router.history.canGoBack()) {
          router.history.back();
          return;
        }
        navigate({
          to: "/teams/$teamSlug/repositories/$repoFullPath/issues",
          params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
          search: { state: "Open", page: 1 },
        });
      }}
    />
  );
}
