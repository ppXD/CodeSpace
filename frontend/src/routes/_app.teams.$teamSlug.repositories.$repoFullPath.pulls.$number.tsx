import { createFileRoute, useNavigate } from "@tanstack/react-router";

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

  // URL uses the readable fullPath; the detail component still takes the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <PullRequestDetailRoute
      repoId={repo.id}
      number={number}
      onBack={() => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath/pulls",
        params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) },
        // Land back on the default list view; the prior filter/page lives in
        // the browser's back-stack if the user wants exactly where they came from.
        search: { state: "Open", page: 1 },
      })}
    />
  );
}
