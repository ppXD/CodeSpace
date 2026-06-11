import { createFileRoute } from "@tanstack/react-router";

import { ModelCredentialsPage } from "@/components/modelCredentials/ModelCredentialsPage";

/** Settings → Model credentials. The keys agents authenticate model providers with. */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings/model-credentials")({
  component: ModelCredentialsPage,
});
