import { createFileRoute } from "@tanstack/react-router";

import { CodeBrowserBody } from "@/_imported/ai-code-space/code-browser";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/code")({
  component: CodeRoute,
});

function CodeRoute() {
  const { repoFullPath } = Route.useParams();
  // URL uses the readable fullPath ("acme/postboy.api"); the body takes the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return <CodeBrowserBody repoId={repo.id} />;
}
