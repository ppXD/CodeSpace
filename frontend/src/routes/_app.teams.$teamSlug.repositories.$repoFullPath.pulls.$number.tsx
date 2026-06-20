import { createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";

import { PullRequestDetailRoute } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * PR detail at `/teams/{slug}/repositories/{fullPath}/pulls/{number}`. Number is a
 * path param so the URL stands alone — pasting the link drops you on this PR's
 * detail view without needing the surrounding list state.
 *
 * `$repoFullPath` is the URL-encoded provider fullPath; we decode once at the route
 * edge to recover the readable form and resolve to a UUID for downstream API calls.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/pulls/$number")({
  component: PullRequestDetailPageRoute,
});

function PullRequestDetailPageRoute() {
  // `number` arrives as a string per URL convention; coerce at the edge so the
  // downstream component never has to know the URL form.
  const { teamSlug, repoFullPath, number: numberParam } = Route.useParams();
  const number = Number(numberParam);
  const navigate = useNavigate();
  const router = useRouter();

  // URL uses the readable fullPath; the detail component still takes the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <PullRequestDetailRoute
      repoId={repo.id}
      number={number}
      onBack={() => {
        // Return to EXACTLY where the user came from — the prior list page with its
        // filter + page intact — by walking the history stack. Only fall back to the
        // default list when there's no in-app history (the detail link was opened
        // directly / in a new tab), so we never strand the user on a blank back-stack.
        if (router.history.canGoBack()) {
          router.history.back();
          return;
        }
        navigate({
          to: "/teams/$teamSlug/repositories/$repoFullPath/pulls",
          params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
          search: { state: "Open", page: 1 },
        });
      }}
    />
  );
}
