import { createFileRoute } from "@tanstack/react-router";

import { RepoOverviewBody } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/overview")({
  component: OverviewRoute,
});

function OverviewRoute() {
  const { repoFullPath } = Route.useParams();
  // URL uses the readable fullPath ("acme/postboy.api"); the downstream body still
  // takes the UUID. Resolve via the team-wide repo list, then pass repoId through.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return <RepoOverviewBody repoId={repo.id} />;
}
