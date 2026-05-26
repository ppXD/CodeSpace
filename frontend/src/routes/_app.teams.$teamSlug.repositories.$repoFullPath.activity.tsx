import { createFileRoute } from "@tanstack/react-router";

import { RepoStubBody } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/activity")({
  component: ActivityRoute,
});

function ActivityRoute() {
  const { repoFullPath } = Route.useParams();
  // URL uses the readable fullPath; the stub body still takes the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return <RepoStubBody repoId={repo.id} kind="activity" />;
}
