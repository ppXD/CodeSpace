import { createFileRoute } from "@tanstack/react-router";

import { RepositoryWebhooksPanel } from "@/components/repositories/RepositoryWebhooksPanel";
import { useProviderInstances } from "@/hooks/use-credentials";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath/webhooks")({
  component: WebhooksRoute,
});

function WebhooksRoute() {
  const { repoFullPath } = Route.useParams();
  // URL uses the readable fullPath ("acme/postboy.api"); the panel's API calls take the UUID.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);
  const instances = useProviderInstances();

  if (isLoading) return null;
  if (notFound || !repo) return null;

  const provider = instances.data?.find((i) => i.id === repo.providerInstanceId)?.provider;

  // Every word on this page is the provider's — GitLab says "Secret token" where GitHub says
  // "Secret" — so it waits for the lookup rather than painting one provider's labels for the other.
  if (!provider) return null;

  return <RepositoryWebhooksPanel repositoryId={repo.id} fullPath={repo.fullPath} provider={provider} />;
}
